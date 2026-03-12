//go:build windows

package main

import (
	"bufio"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"math/big"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	socks5 "github.com/things-go/go-socks5"
	"github.com/kardianos/service"
)

// ─────────────────────────────────────────────
// 配置
// ─────────────────────────────────────────────

type Config struct {
	Listen   string
	Port     string
	Username string
	Password string
	UseTLS   bool
	CertFile string
	KeyFile  string
	LogFile  string
	TLSSAN   string
	Verbose  bool // 是否输出 socks5 连接级别的网络日志
	MaxConns int  // 最大并发连接数（0 = 使用默认值 2048）
}

var cfg Config

const handshakeTimeout = 10 * time.Second

// ─────────────────────────────────────────────
// Windows 服务 封装
// ─────────────────────────────────────────────

type program struct {
	mu       sync.Mutex
	listener net.Listener // 保存监听器以便 Stop() 优雅关闭
}

func (p *program) Start(s service.Service) error {
	logger.Printf("[INFO] 服务启动, 监听=%s 端口=%s TLS=%v MaxConns=%d",
		cfg.Listen, cfg.Port, cfg.UseTLS, effectiveMaxConns())
	go p.run()
	return nil
}

func (p *program) Stop(s service.Service) error {
	logger.Println("[INFO] 服务停止")
	p.mu.Lock()
	ln := p.listener
	p.listener = nil
	p.mu.Unlock()
	if ln != nil {
		_ = ln.Close()
	}
	flushLogger()
	return nil
}

func effectiveMaxConns() int {
	if cfg.MaxConns <= 0 {
		return 2048
	}
	return cfg.MaxConns
}

func (p *program) run() {
	creds := socks5.StaticCredentials{cfg.Username: cfg.Password}
	auth := socks5.UserPassAuthenticator{Credentials: creds}

	var socks5Log *log.Logger
	if cfg.Verbose {
		socks5Log = log.New(logWriter(), "[socks5] ", 0)
	} else {
		socks5Log = log.New(io.Discard, "", 0)
	}
	srv := socks5.NewServer(
		socks5.WithAuthMethods([]socks5.Authenticator{auth}),
		socks5.WithLogger(socks5.NewLogger(socks5Log)),
		socks5.WithConnectMiddleware(clearConnDeadlineMiddleware),
		socks5.WithBindMiddleware(clearConnDeadlineMiddleware),
		socks5.WithAssociateMiddleware(clearConnDeadlineMiddleware),
		socks5.WithDial(func(ctx context.Context, network, addr string) (net.Conn, error) {
			conn, err := (&net.Dialer{}).DialContext(ctx, network, addr)
			if err != nil {
				return nil, err
			}
			// 禁用 Nagle 算法：确保 Telnet 等交互协议的单字节数据（如退格键）立即发出，不被积攒
			if tc, ok := conn.(*net.TCPConn); ok {
				_ = tc.SetNoDelay(true)
			}
			return conn, nil
		}),
	)

	addr := effectiveListenAddr()

	var tlsCfg *tls.Config
	if cfg.UseTLS {
		var err error
		tlsCfg, err = buildTLSConfig()
		if err != nil {
			logger.Printf("[ERROR] 构建TLS配置失败: %v", err)
			return
		}
		logger.Printf("[INFO] TLS SOCKS5 监听 %s（自动识别 TLS/裸连接）", addr)
	} else {
		logger.Printf("[INFO] 裸 SOCKS5 监听 %s", addr)
	}

	ln, err := newSmartListener(addr, tlsCfg, effectiveMaxConns())
	if err != nil {
		logger.Printf("[ERROR] 监听失败: %v", err)
		return
	}

	p.mu.Lock()
	p.listener = ln
	p.mu.Unlock()

	if err := srv.Serve(ln); err != nil {
		logger.Printf("[INFO] 服务退出: %v", err)
	}

	p.mu.Lock()
	if p.listener == ln {
		p.listener = nil
	}
	p.mu.Unlock()
}

// ─────────────────────────────────────────────
// smartListener：同端口自动嗅探 TLS / 裸 SOCKS5
//
// 架构说明：
//   协议嗅探（Peek）属于"连接生命周期"操作，其错误不能从 Accept() 返回，
//   否则 go-socks5 的 Serve() 会把它误判为监听器级别的致命错误而停服。
//   正确做法：Accept() 只负责交付"已嗅探完毕的可用连接"，
//   嗅探工作在独立 goroutine 完成后通过 channel 送入，
//   两种错误在此处被彻底分离。
// ─────────────────────────────────────────────

// peekConn 仅缓存嗅探阶段读取的那 1 个字节，其余所有读写直穿原始 TCP socket。
// 不使用 bufio，彻底避免双层缓冲导致 Telnet 等交互协议逐字节数据被憋在内存里。
type peekConn struct {
	net.Conn
	firstByte [1]byte
	consumed  bool
}

func (c *peekConn) Read(b []byte) (int, error) {
	if !c.consumed {
		if len(b) == 0 {
			return 0, nil
		}
		b[0] = c.firstByte[0]
		c.consumed = true
		return 1, nil
	}
	return c.Conn.Read(b)
}

// limitedConn 在 Close() 时归还信号量，确保并发连接数上限始终有效。
type limitedConn struct {
	net.Conn
	once sync.Once
	sem  chan struct{}
}

func (c *limitedConn) Close() error {
	err := c.Conn.Close()
	c.once.Do(func() { <-c.sem })
	return err
}

type smartListener struct {
	inner     net.Listener
	tlsCfg    *tls.Config
	connCh    chan net.Conn // 已就绪的连接
	errCh     chan error    // 监听器级别的错误（只会收到一次）
	done      chan struct{} // 关闭信号
	closeOnce sync.Once
	sem       chan struct{} // 并发连接限制信号量
}

func newSmartListener(addr string, tlsCfg *tls.Config, maxConns int) (net.Listener, error) {
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return nil, err
	}
	sl := &smartListener{
		inner:  ln,
		tlsCfg: tlsCfg,
		connCh: make(chan net.Conn, 128),
		errCh:  make(chan error, 1),
		done:   make(chan struct{}),
		sem:    make(chan struct{}, maxConns),
	}
	go sl.acceptLoop()
	return sl, nil
}

// acceptLoop 在后台持续接受 TCP 连接并分发到 goroutine 做协议嗅探。
// 只有 inner.Accept() 出错（监听器本身故障）才写入 errCh 并退出。
func (sl *smartListener) acceptLoop() {
	for {
		conn, err := sl.inner.Accept()
		if err != nil {
			select {
			case <-sl.done:
				// 正常关闭，不需要记录错误
			default:
				sl.errCh <- err
			}
			return
		}
		if tc, ok := conn.(*net.TCPConn); ok {
			_ = tc.SetKeepAlive(true)
			_ = tc.SetKeepAlivePeriod(30 * time.Second)
			_ = tc.SetNoDelay(true) // 禁用 Nagle，提升 Telnet 等交互协议响应性
		}
		// 连接数限制：超出上限直接拒绝，避免耗尽系统资源
		select {
		case sl.sem <- struct{}{}:
		default:
			logger.Printf("[WARN] 连接数已达上限 %d，拒绝来自 %s 的连接", cap(sl.sem), conn.RemoteAddr())
			_ = conn.Close()
			continue
		}
		go sl.sniff(&limitedConn{Conn: conn, sem: sl.sem})
	}
}

// sniff 偷看首字节判断协议，完成后将连接送入 connCh。
// 嗅探失败（连接已断开/超时）时静默关闭，不影响监听器。
func (sl *smartListener) sniff(conn net.Conn) {
	_ = conn.SetDeadline(time.Now().Add(handshakeTimeout))
	if sl.tlsCfg == nil {
		select {
		case sl.connCh <- conn:
		case <-sl.done:
			_ = conn.Close()
		}
		return
	}
	// 嗅探超时 3s，防止客户端连上来什么都不发导致 goroutine 堆积
	_ = conn.SetReadDeadline(time.Now().Add(3 * time.Second))
	pc := &peekConn{Conn: conn}
	_, err := io.ReadFull(conn, pc.firstByte[:])
	// 恢复完整连接 deadline，继续覆盖 TLS 握手 + SOCKS greeting + 用户认证 + request 解析
	_ = conn.SetDeadline(time.Now().Add(handshakeTimeout))
	if err != nil {
		_ = conn.Close()
		return
	}

	var ready net.Conn
	if pc.firstByte[0] == 0x16 {
		// TLS ClientHello，升级为 TLS 连接再送入
		ready = tls.Server(pc, sl.tlsCfg)
	} else {
		// 裸 SOCKS5（0x05）或其他，直接送入
		ready = pc
	}

	select {
	case sl.connCh <- ready:
	case <-sl.done:
		_ = ready.Close()
	}
}

// Accept 实现 net.Listener 接口。
func (sl *smartListener) Accept() (net.Conn, error) {
	select {
	case conn := <-sl.connCh:
		return conn, nil
	case err := <-sl.errCh:
		return nil, err
	case <-sl.done:
		return nil, net.ErrClosed
	}
}

func (sl *smartListener) Close() error {
	var err error
	sl.closeOnce.Do(func() {
		close(sl.done)       // 通知所有 sniff goroutine 退出
		err = sl.inner.Close() // 关闭底层监听，使 acceptLoop 解除阻塞
	})
	return err
}

func (sl *smartListener) Addr() net.Addr { return sl.inner.Addr() }

// ─────────────────────────────────────────────
// TLS 配置（自签或加载外部证书）
// ─────────────────────────────────────────────

func buildTLSConfig() (*tls.Config, error) {
	var cert tls.Certificate
	var err error

	if (cfg.CertFile == "") != (cfg.KeyFile == "") {
		return nil, errors.New("启用 TLS 时，-cert 和 -key 必须同时提供")
	}

	if cfg.CertFile != "" && cfg.KeyFile != "" {
		cert, err = tls.LoadX509KeyPair(cfg.CertFile, cfg.KeyFile)
		if err != nil {
			return nil, fmt.Errorf("加载证书失败: %w", err)
		}
		logger.Println("[INFO] 使用外部证书文件")
	} else {
		cert, err = loadOrGenerateSelfSignedCert()
		if err != nil {
			return nil, fmt.Errorf("准备自签证书失败: %w", err)
		}
	}

	return &tls.Config{
		Certificates: []tls.Certificate{cert},
		MinVersion:   tls.VersionTLS12,
		CipherSuites: []uint16{
			// ECDSA 套件优先（与 P-256 密钥匹配，握手性能最佳）
			tls.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
			tls.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
			tls.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305,
			// RSA 套件兼容（供外部证书使用）
			tls.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
			tls.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
			tls.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305,
		},
	}, nil
}

// loadOrGenerateSelfSignedCert 优先从磁盘加载已有证书（未过期则复用），
// 否则生成新的 ECDSA P-256 证书并保存到磁盘。
func loadOrGenerateSelfSignedCert() (tls.Certificate, error) {
	certPath := filepath.Join(exeDir(), "proxy-cert.pem")
	keyPath := filepath.Join(exeDir(), "proxy-key.pem")

	if data, err := os.ReadFile(certPath); err == nil {
		if block, _ := pem.Decode(data); block != nil {
			if leaf, err := x509.ParseCertificate(block.Bytes); err == nil {
				// 有效期剩余 30 天以上才复用
				if time.Now().Before(leaf.NotAfter.Add(-30 * 24 * time.Hour)) {
					if cert, err := tls.LoadX509KeyPair(certPath, keyPath); err == nil {
						logger.Printf("[INFO] 复用自签证书（有效至 %s）", leaf.NotAfter.Format("2006-01-02"))
						return cert, nil
					}
				}
			}
		}
	}

	return generateAndSaveCert(certPath, keyPath)
}

func generateAndSaveCert(certPath, keyPath string) (tls.Certificate, error) {
	// 使用 ECDSA P-256：密钥生成比 RSA-2048 快约 100 倍，握手性能更优
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}
	dnsNames, ipAddrs := selfSignedSANs()
	template := &x509.Certificate{
		SerialNumber: big.NewInt(time.Now().UnixNano()),
		Subject:      pkix.Name{CommonName: "WinJumpProxy"},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(10 * 365 * 24 * time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		DNSNames:     dnsNames,
		IPAddresses:  ipAddrs,
	}
	certDER, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		return tls.Certificate{}, err
	}
	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: certDER})
	keyDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		return tls.Certificate{}, err
	}
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER})

	// 持久化到磁盘，下次启动直接复用，避免重复生成开销
	if err := os.WriteFile(certPath, certPEM, 0600); err != nil {
		logger.Printf("[WARN] 无法保存证书到磁盘: %v", err)
	}
	if err := os.WriteFile(keyPath, keyPEM, 0600); err != nil {
		logger.Printf("[WARN] 无法保存私钥到磁盘: %v", err)
	}
	logger.Println("[INFO] 已生成新的自签TLS证书（ECDSA P-256）并保存到磁盘")
	return tls.X509KeyPair(certPEM, keyPEM)
}

// ─────────────────────────────────────────────
// 防火墙规则
// ─────────────────────────────────────────────

func addFirewallRule(port string) error {
	if !shouldManageFirewall() {
		logger.Printf("[INFO] 监听地址为本地回环（%s），跳过防火墙规则", cfg.Listen)
		return nil
	}

	_ = removeFirewallRules()

	exePath, _ := os.Executable()
	ruleName := "WinJumpProxy-SOCKS5-" + port

	_ = exec.Command("netsh", "advfirewall", "firewall", "delete", "rule",
		"name="+ruleName).Run()

	cmd := exec.Command("netsh", "advfirewall", "firewall", "add", "rule",
		"name="+ruleName,
		"dir=in",
		"action=allow",
		"protocol=TCP",
		"localport="+port,
		"program="+exePath,
		"enable=yes",
		"description=WinJumpProxy SOCKS5 inbound",
	)
	out, err := cmd.CombinedOutput()
	if err != nil {
		logger.Printf("[WARN] 添加防火墙规则失败: %v | %s", err, out)
		return err
	}
	logger.Printf("[INFO] 防火墙规则已添加: port=%s", port)
	return nil
}

func removeFirewallRules() error {
	exePath, _ := os.Executable()
	out, err := exec.Command("netsh", "advfirewall", "firewall", "delete", "rule",
		"name=all", "program="+exePath).CombinedOutput()
	if err != nil {
		logger.Printf("[WARN] 删除防火墙规则失败: %v | %s", err, out)
		return err
	}
	logger.Printf("[INFO] 已删除当前程序关联的防火墙规则")
	return nil
}

// ─────────────────────────────────────────────
// 服务定义
// ─────────────────────────────────────────────

var svcConfig = &service.Config{
	Name:        "WinJumpProxy",
	DisplayName: "WinJumpProxy SOCKS5 Service",
	Description: "高性能 SOCKS5 代理，支持 TLS 伪装，用于内网穿透跳板。",
	Option: service.KeyValue{
		"StartType": "automatic",
	},
}

// ─────────────────────────────────────────────
// main
// ─────────────────────────────────────────────

var logger *log.Logger

var knownSubcmds = map[string]bool{
	"install": true, "uninstall": true, "start": true, "stop": true, "run": true,
}

func registerFlags(fs *flag.FlagSet) {
	fs.StringVar(&cfg.Listen, "listen", "0.0.0.0", "SOCKS5 监听地址（如 0.0.0.0 / 127.0.0.1 / ::）")
	fs.StringVar(&cfg.Port, "port", "1080", "SOCKS5 监听端口")
	fs.StringVar(&cfg.Username, "user", "admin", "认证用户名")
	fs.StringVar(&cfg.Password, "pass", "changeme", "认证密码")
	fs.BoolVar(&cfg.UseTLS, "tls", false, "是否启用 TLS 封装")
	fs.StringVar(&cfg.CertFile, "cert", "", "TLS 证书文件路径（空=自动生成自签证书）")
	fs.StringVar(&cfg.KeyFile, "key", "", "TLS 私钥文件路径")
	fs.StringVar(&cfg.LogFile, "log", "", "日志文件路径（空=exe同级目录 proxy.log）")
	fs.StringVar(&cfg.TLSSAN, "san", "", "自签证书额外 SAN（逗号分隔，可填域名/IP）")
	fs.BoolVar(&cfg.Verbose, "verbose", false, "输出 socks5 连接级调试日志（默认关闭）")
	fs.IntVar(&cfg.MaxConns, "maxconns", 0, "最大并发连接数（默认 2048）")
}

func main() {
	rawArgs := os.Args[1:]

	subcmd := ""
	flagArgs := rawArgs

	if len(rawArgs) > 0 && knownSubcmds[rawArgs[0]] {
		subcmd = rawArgs[0]
		flagArgs = rawArgs[1:]
	}

	fs := flag.NewFlagSet("proxy", flag.ExitOnError)
	registerFlags(fs)
	_ = fs.Parse(flagArgs)

	initLogger()
	defer closeLogger()

	if shouldValidateConfig(subcmd) {
		if err := validateConfig(); err != nil {
			logger.Fatalf("[FATAL] 配置无效: %v", err)
		}
	}

	prg := &program{}
	svcCfg := *svcConfig
	svcCfg.Arguments = buildServiceArguments()
	svc, err := service.New(prg, &svcCfg)
	if err != nil {
		logger.Fatalf("[FATAL] 创建服务失败: %v", err)
	}

	switch subcmd {
	case "install":
		if err := svc.Install(); err != nil {
			logger.Fatalf("[FATAL] 安装服务失败: %v", err)
		}
		if err := addFirewallRule(cfg.Port); err != nil {
			logger.Printf("[WARN] 服务已安装，但自动配置防火墙失败，请手动放行端口 %s", cfg.Port)
		}
		logger.Println("[INFO] 服务已安装: WinJumpProxy")
		fmt.Println("服务安装成功，使用 'sc start WinJumpProxy' 启动")
	case "uninstall":
		if err := svc.Uninstall(); err != nil {
			logger.Fatalf("[FATAL] 卸载服务失败: %v", err)
		}
		_ = removeFirewallRules()
		logger.Println("[INFO] 服务已卸载")
		fmt.Println("服务卸载成功")
	case "start":
		if err := svc.Start(); err != nil {
			logger.Fatalf("[FATAL] 启动服务失败: %v", err)
		}
		fmt.Println("服务已启动")
	case "stop":
		if err := svc.Stop(); err != nil {
			logger.Fatalf("[FATAL] 停止服务失败: %v", err)
		}
		fmt.Println("服务已停止")
	case "run":
		logger.Println("[INFO] 前台模式运行")
		prg.run()
	case "":
		if err := svc.Run(); err != nil {
			logger.Fatalf("[FATAL] 服务运行失败: %v", err)
		}
	default:
		fmt.Fprintf(os.Stderr, "未知命令: %s\n用法: proxy.exe [install|uninstall|start|stop|run] [选项]\n", subcmd)
		os.Exit(1)
	}
}

func shouldValidateConfig(subcmd string) bool {
	switch subcmd {
	case "", "install", "run":
		return true
	default:
		return false
	}
}

func effectiveListenAddr() string {
	return net.JoinHostPort(strings.TrimSpace(cfg.Listen), cfg.Port)
}

func shouldManageFirewall() bool {
	return !isLoopbackHost(cfg.Listen)
}

func isLoopbackHost(host string) bool {
	host = strings.TrimSpace(host)
	if host == "" {
		return false
	}
	if strings.EqualFold(host, "localhost") {
		return true
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

func validateConfig() error {
	if strings.TrimSpace(cfg.Listen) == "" {
		return errors.New("监听地址不能为空")
	}
	if port, err := strconv.Atoi(cfg.Port); err != nil || port < 1 || port > 65535 {
		return fmt.Errorf("端口无效: %s", cfg.Port)
	}
	if strings.TrimSpace(cfg.Username) == "" {
		return errors.New("用户名不能为空")
	}
	if strings.TrimSpace(cfg.Password) == "" {
		return errors.New("密码不能为空")
	}
	if (cfg.CertFile == "") != (cfg.KeyFile == "") {
		return errors.New("启用外部证书时，-cert 和 -key 必须同时提供")
	}
	if cfg.Username == "admin" && cfg.Password == "changeme" && !isLoopbackHost(cfg.Listen) {
		return errors.New("监听非回环地址时不能使用默认账号 admin/changeme，请显式设置 -user/-pass")
	}
	return nil
}

func buildServiceArguments() []string {
	args := []string{
		"-listen=" + cfg.Listen,
		"-port=" + cfg.Port,
		"-user=" + cfg.Username,
		"-pass=" + cfg.Password,
		"-tls=" + strconv.FormatBool(cfg.UseTLS),
		"-verbose=" + strconv.FormatBool(cfg.Verbose),
	}
	if cfg.CertFile != "" {
		args = append(args, "-cert="+cfg.CertFile)
	}
	if cfg.KeyFile != "" {
		args = append(args, "-key="+cfg.KeyFile)
	}
	if cfg.LogFile != "" {
		args = append(args, "-log="+cfg.LogFile)
	}
	if cfg.TLSSAN != "" {
		args = append(args, "-san="+cfg.TLSSAN)
	}
	if cfg.MaxConns > 0 {
		args = append(args, "-maxconns="+strconv.Itoa(cfg.MaxConns))
	}
	return args
}

func clearConnDeadlineMiddleware(_ context.Context, writer io.Writer, _ *socks5.Request) error {
	if conn, ok := writer.(net.Conn); ok {
		_ = conn.SetDeadline(time.Time{})
	}
	return nil
}

func selfSignedSANs() ([]string, []net.IP) {
	dnsSet := map[string]struct{}{
		"localhost":    {},
		"WinJumpProxy": {},
	}
	if host, err := os.Hostname(); err == nil && strings.TrimSpace(host) != "" {
		dnsSet[host] = struct{}{}
	}

	ipSet := map[string]net.IP{
		net.IPv4(127, 0, 0, 1).String(): net.IPv4(127, 0, 0, 1),
		net.IPv6loopback.String():       net.IPv6loopback,
	}

	addSAN := func(item string) {
		item = strings.TrimSpace(item)
		if item == "" {
			return
		}
		if ip := net.ParseIP(item); ip != nil {
			if !ip.IsUnspecified() {
				ipSet[ip.String()] = ip
			}
			return
		}
		dnsSet[item] = struct{}{}
	}

	addSAN(cfg.Listen)
	for _, item := range strings.Split(cfg.TLSSAN, ",") {
		addSAN(item)
	}

	dnsNames := make([]string, 0, len(dnsSet))
	for name := range dnsSet {
		dnsNames = append(dnsNames, name)
	}

	ipAddrs := make([]net.IP, 0, len(ipSet))
	for _, ip := range ipSet {
		ipAddrs = append(ipAddrs, ip)
	}
	return dnsNames, ipAddrs
}

// ─────────────────────────────────────────────
// 日志（带缓冲的滚动写文件）
// ─────────────────────────────────────────────

var (
	_logFile *os.File
	_logBuf  *syncBufWriter
)

// syncBufWriter 是线程安全的缓冲写入器。
// log.Logger 内部有 mutex 保证 Write 串行，flush goroutine 也需要同一把锁。
type syncBufWriter struct {
	mu  sync.Mutex
	buf *bufio.Writer
	f   *os.File
}

func (w *syncBufWriter) Write(p []byte) (n int, err error) {
	w.mu.Lock()
	n, err = w.buf.Write(p)
	w.mu.Unlock()
	return
}

func (w *syncBufWriter) Flush() {
	w.mu.Lock()
	_ = w.buf.Flush()
	w.mu.Unlock()
}

func (w *syncBufWriter) Close() {
	w.mu.Lock()
	_ = w.buf.Flush()
	_ = w.f.Close()
	w.mu.Unlock()
}

func exeDir() string {
	exe, err := os.Executable()
	if err != nil {
		return "."
	}
	return filepath.Dir(exe)
}

func logWriter() io.Writer {
	if _logBuf != nil {
		return _logBuf
	}
	return os.Stderr
}

func flushLogger() {
	if _logBuf != nil {
		_logBuf.Flush()
	}
}

// rotateLog 将现有日志文件滚动：.log → .log.1 → .log.2 → .log.3，最多保留 3 个备份。
func rotateLog(logPath string) {
	_ = os.Remove(logPath + ".3")
	for i := 2; i >= 1; i-- {
		_ = os.Rename(
			fmt.Sprintf("%s.%d", logPath, i),
			fmt.Sprintf("%s.%d", logPath, i+1),
		)
	}
	_ = os.Rename(logPath, logPath+".1")
}

func initLogger() {
	logPath := cfg.LogFile
	if logPath == "" {
		logPath = filepath.Join(exeDir(), "proxy.log")
	}

	if fi, err := os.Stat(logPath); err == nil && fi.Size() > 10*1024*1024 {
		rotateLog(logPath) // 超过 10MB 则滚动，保留最近 3 个备份
	}

	f, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		logger = log.New(os.Stderr, "", log.LstdFlags)
		return
	}

	_logBuf = &syncBufWriter{buf: bufio.NewWriterSize(f, 32*1024), f: f}
	logger = log.New(_logBuf, "", log.LstdFlags)

	// 每 5 秒定期刷盘，确保日志不因缓冲而丢失
	go func() {
		ticker := time.NewTicker(5 * time.Second)
		defer ticker.Stop()
		for range ticker.C {
			if _logBuf == nil {
				return
			}
			_logBuf.Flush()
		}
	}()
}

func closeLogger() {
	if _logBuf != nil {
		_logBuf.Close()
		_logBuf = nil
	}
}
