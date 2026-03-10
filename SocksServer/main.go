//go:build windows

package main

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"flag"
	"fmt"
	"io"
	"log"
	"math/big"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"time"

	socks5 "github.com/things-go/go-socks5"
	"github.com/kardianos/service"
)

// ─────────────────────────────────────────────
// 配置
// ─────────────────────────────────────────────

type Config struct {
	Port     string
	Username string
	Password string
	UseTLS   bool
	CertFile string
	KeyFile  string
	LogFile  string
	Verbose  bool // 是否输出 socks5 连接级别的网络日志
}

var cfg Config

// ─────────────────────────────────────────────
// Windows 服务 封装
// ─────────────────────────────────────────────

type program struct {
	server *socks5.Server
	cancel context.CancelFunc
}

func (p *program) Start(s service.Service) error {
	logger.Printf("[INFO] 服务启动, 端口=%s TLS=%v", cfg.Port, cfg.UseTLS)
	go p.run()
	return nil
}

func (p *program) Stop(s service.Service) error {
	logger.Println("[INFO] 服务停止")
	if p.cancel != nil {
		p.cancel()
	}
	return nil
}

func (p *program) run() {
	runtime.GOMAXPROCS(runtime.NumCPU())

	creds := socks5.StaticCredentials{cfg.Username: cfg.Password}
	auth := socks5.UserPassAuthenticator{Credentials: creds}

	// 默认丢弃 socks5 连接级日志（连接被目标服务器重置等正常网络事件）
	// 加 -verbose 后才输出，用于调试
	var socks5Log *log.Logger
	if cfg.Verbose {
		socks5Log = log.New(logWriter(), "[socks5] ", 0)
	} else {
		socks5Log = log.New(io.Discard, "", 0)
	}
	srv := socks5.NewServer(
		socks5.WithAuthMethods([]socks5.Authenticator{auth}),
		socks5.WithLogger(socks5.NewLogger(socks5Log)),
	)
	p.server = srv

	addr := "0.0.0.0:" + cfg.Port

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

	ln, err := newSmartListener(addr, tlsCfg)
	if err != nil {
		logger.Printf("[ERROR] 监听失败: %v", err)
		return
	}
	if err := srv.Serve(ln); err != nil {
		logger.Printf("[ERROR] 服务异常退出: %v", err)
	}
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

type smartListener struct {
	inner  net.Listener
	tlsCfg *tls.Config
	connCh chan net.Conn  // 已就绪的连接
	errCh  chan error     // 监听器级别的错误（只会收到一次）
}

func newSmartListener(addr string, tlsCfg *tls.Config) (net.Listener, error) {
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return nil, err
	}
	sl := &smartListener{
		inner:  ln,
		tlsCfg: tlsCfg,
		connCh: make(chan net.Conn, 128),
		errCh:  make(chan error, 1),
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
			sl.errCh <- err // 监听器级别错误，通知 Accept() 返回
			return
		}
		if tc, ok := conn.(*net.TCPConn); ok {
			_ = tc.SetKeepAlive(true)
			_ = tc.SetKeepAlivePeriod(30 * time.Second)
		}
		// 协议嗅探在独立 goroutine 完成，不阻塞 acceptLoop
		go sl.sniff(conn)
	}
}

// sniff 偷看首字节判断协议，完成后将连接送入 connCh。
// 嗅探失败（连接已断开）时静默关闭，不影响监听器。
func (sl *smartListener) sniff(conn net.Conn) {
	if sl.tlsCfg == nil {
		sl.connCh <- conn
		return
	}
	// 设置嗅探超时，防止客户端连上来什么都不发导致 goroutine 泄漏
	_ = conn.SetReadDeadline(time.Now().Add(10 * time.Second))
	// io.ReadFull 精确读取 1 字节，不多读，不引入任何额外缓冲层
	pc := &peekConn{Conn: conn}
	_, err := io.ReadFull(conn, pc.firstByte[:])
	_ = conn.SetReadDeadline(time.Time{}) // 清除 deadline，后续由 socks5 库自己管理
	if err != nil {
		// 连接级别错误：对端已断开或超时，静默关闭
		_ = conn.Close()
		return
	}
	if pc.firstByte[0] == 0x16 {
		// TLS ClientHello，升级为 TLS 连接再送入
		sl.connCh <- tls.Server(pc, sl.tlsCfg)
	} else {
		// 裸 SOCKS5（0x05）或其他，直接送入
		sl.connCh <- pc
	}
}

// Accept 实现 net.Listener 接口。
// 从 connCh 取已就绪连接，或从 errCh 取监听器级别错误。
func (sl *smartListener) Accept() (net.Conn, error) {
	select {
	case conn := <-sl.connCh:
		return conn, nil
	case err := <-sl.errCh:
		return nil, err
	}
}

func (sl *smartListener) Close() error   { return sl.inner.Close() }
func (sl *smartListener) Addr() net.Addr { return sl.inner.Addr() }

// ─────────────────────────────────────────────
// TLS 配置（自签或加载外部证书）
// ─────────────────────────────────────────────

func buildTLSConfig() (*tls.Config, error) {
	var cert tls.Certificate
	var err error

	if cfg.CertFile != "" && cfg.KeyFile != "" {
		cert, err = tls.LoadX509KeyPair(cfg.CertFile, cfg.KeyFile)
		if err != nil {
			return nil, fmt.Errorf("加载证书失败: %w", err)
		}
		logger.Println("[INFO] 使用外部证书文件")
	} else {
		cert, err = generateSelfSignedCert()
		if err != nil {
			return nil, fmt.Errorf("生成自签证书失败: %w", err)
		}
		logger.Println("[INFO] 已生成自签TLS证书")
	}

	return &tls.Config{
		Certificates: []tls.Certificate{cert},
		MinVersion:   tls.VersionTLS12,
		CipherSuites: []uint16{
			tls.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
			tls.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
			tls.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305,
		},
	}, nil
}

func generateSelfSignedCert() (tls.Certificate, error) {
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return tls.Certificate{}, err
	}
	template := &x509.Certificate{
		SerialNumber: big.NewInt(time.Now().UnixNano()),
		Subject:      pkix.Name{CommonName: "WinJumpProxy"},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(10 * 365 * 24 * time.Hour),
		KeyUsage:     x509.KeyUsageKeyEncipherment | x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	certDER, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		return tls.Certificate{}, err
	}
	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: certDER})
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)})
	return tls.X509KeyPair(certPEM, keyPEM)
}

// ─────────────────────────────────────────────
// 防火墙规则
// ─────────────────────────────────────────────

func addFirewallRule(port string) {
	exePath, _ := os.Executable()
	ruleName := "WinJumpProxy-SOCKS5-" + port

	// 先删除同名旧规则，再添加新规则
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
	} else {
		logger.Printf("[INFO] 防火墙规则已添加: port=%s", port)
	}
}

func removeFirewallRule(port string) {
	ruleName := "WinJumpProxy-SOCKS5-" + port
	_ = exec.Command("netsh", "advfirewall", "firewall", "delete", "rule",
		"name="+ruleName).Run()
	logger.Printf("[INFO] 防火墙规则已移除: port=%s", port)
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

// knownSubcmds 用于判断第一个参数是否是子命令
var knownSubcmds = map[string]bool{
	"install": true, "uninstall": true, "start": true, "stop": true, "run": true,
}

func registerFlags(fs *flag.FlagSet) {
	fs.StringVar(&cfg.Port, "port", "1080", "SOCKS5 监听端口")
	fs.StringVar(&cfg.Username, "user", "admin", "认证用户名")
	fs.StringVar(&cfg.Password, "pass", "changeme", "认证密码")
	fs.BoolVar(&cfg.UseTLS, "tls", false, "是否启用 TLS 封装")
	fs.StringVar(&cfg.CertFile, "cert", "", "TLS 证书文件路径（空=自动生成自签证书）")
	fs.StringVar(&cfg.KeyFile, "key", "", "TLS 私钥文件路径")
	fs.StringVar(&cfg.LogFile, "log", "", "日志文件路径（空=exe同级目录 proxy.log）")
	fs.BoolVar(&cfg.Verbose, "verbose", false, "输出 socks5 连接级调试日志（默认关闭）")
}

func main() {
	rawArgs := os.Args[1:]

	subcmd := ""
	flagArgs := rawArgs

	// 若第一个参数是子命令，则分离子命令与其后的 flag 参数
	if len(rawArgs) > 0 && knownSubcmds[rawArgs[0]] {
		subcmd = rawArgs[0]
		flagArgs = rawArgs[1:]
	}

	// 用独立 FlagSet 解析，避免 flag 包遇到子命令停止解析的问题
	fs := flag.NewFlagSet("proxy", flag.ExitOnError)
	registerFlags(fs)
	_ = fs.Parse(flagArgs)

	// 日志初始化
	initLogger()
	defer closeLogger()

	prg := &program{}
	svc, err := service.New(prg, svcConfig)
	if err != nil {
		logger.Fatalf("[FATAL] 创建服务失败: %v", err)
	}

	switch subcmd {
	case "install":
		addFirewallRule(cfg.Port)
		if err := svc.Install(); err != nil {
			logger.Fatalf("[FATAL] 安装服务失败: %v", err)
		}
		logger.Println("[INFO] 服务已安装: WinJumpProxy")
		fmt.Println("服务安装成功，使用 'sc start WinJumpProxy' 启动")
	case "uninstall":
		if err := svc.Uninstall(); err != nil {
			logger.Fatalf("[FATAL] 卸载服务失败: %v", err)
		}
		removeFirewallRule(cfg.Port)
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
		// 无子命令：由 SCM 作为服务调用
		if err := svc.Run(); err != nil {
			logger.Fatalf("[FATAL] 服务运行失败: %v", err)
		}
	default:
		fmt.Fprintf(os.Stderr, "未知命令: %s\n用法: proxy.exe [install|uninstall|start|stop|run] [选项]\n", subcmd)
		os.Exit(1)
	}
}

// ─────────────────────────────────────────────
// 日志（滚动写文件）
// ─────────────────────────────────────────────

var _logFile *os.File

func exeDir() string {
	exe, err := os.Executable()
	if err != nil {
		return "."
	}
	return filepath.Dir(exe)
}

func logWriter() *os.File {
	if _logFile != nil {
		return _logFile
	}
	return os.Stderr
}

func initLogger() {
	logPath := cfg.LogFile
	if logPath == "" {
		logPath = filepath.Join(exeDir(), "proxy.log")
	}

	// 简单滚动：超过 10MB 则截断
	if fi, err := os.Stat(logPath); err == nil && fi.Size() > 10*1024*1024 {
		_ = os.Rename(logPath, logPath+".bak")
	}

	f, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
	if err != nil {
		logger = log.New(os.Stderr, "", log.LstdFlags)
		return
	}
	_logFile = f
	logger = log.New(f, "", log.LstdFlags)
}

func closeLogger() {
	if _logFile != nil {
		_ = _logFile.Close()
	}
}
