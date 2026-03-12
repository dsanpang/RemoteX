using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace RemoteX;

internal sealed class ServerRepository
{
    private readonly string _databasePath;
    private bool _initialized;
    private bool _portForwardsInitialized;
    private bool _quickCommandsInitialized;

    private const string CreateTableSql =
        "CREATE TABLE IF NOT EXISTS Servers (" +
        "Id INTEGER PRIMARY KEY AUTOINCREMENT," +
        "Name TEXT NOT NULL," +
        "IP TEXT NOT NULL," +
        "Port INTEGER NOT NULL," +
        "Username TEXT," +
        "Password TEXT," +
        "Description TEXT," +
        "GroupName TEXT DEFAULT ''," +
        "SortOrder INTEGER DEFAULT 0," +
        "Protocol TEXT DEFAULT 'RDP'," +
        "SshPrivateKeyPath TEXT DEFAULT ''," +
        "UseSocksProxy INTEGER DEFAULT 0," +
        "SocksProxyName TEXT DEFAULT ''," +
        "SocksProxyId TEXT DEFAULT '')";

    private const string SelectAllSql =
        "SELECT Id,Name,IP,Port,Username,Password,Description,GroupName,SortOrder,Protocol,SshPrivateKeyPath,UseSocksProxy,SocksProxyName,SocksProxyId " +
        "FROM Servers ORDER BY SortOrder,Name";

    public ServerRepository(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? AppPaths.Database
            : databasePath;
    }

    public async Task<List<ServerInfo>> LoadAllAsync()
    {
        await EnsureAsync().ConfigureAwait(false);

        var result = new List<ServerInfo>();
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);

        var cmd = conn.CreateCommand();
        cmd.CommandText = SelectAllSql;

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var storedPw = reader.IsDBNull(5) ? "" : reader.GetString(5);
            result.Add(MapRow(reader, storedPw));
        }

        return result;
    }

    public async Task InsertAsync(ServerInfo server)
    {
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);

        var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO Servers (Name,IP,Port,Username,Password,Description,GroupName,SortOrder,Protocol,SshPrivateKeyPath,UseSocksProxy,SocksProxyName,SocksProxyId) " +
            "VALUES ($n,$i,$p,$u,$pw,$desc,$grp,$ord,$proto,$keyPath,$socks,$socksName,$socksId); SELECT last_insert_rowid();";
        BindParams(cmd, server);
        server.Id = (int)(long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    public async Task UpdateAsync(ServerInfo server)
    {
        if (server.Id <= 0) return;
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);

        var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE Servers SET Name=$n,IP=$i,Port=$p,Username=$u,Password=$pw," +
            "Description=$desc,GroupName=$grp,SortOrder=$ord,Protocol=$proto,SshPrivateKeyPath=$keyPath,UseSocksProxy=$socks,SocksProxyName=$socksName,SocksProxyId=$socksId WHERE Id=$id";
        BindParams(cmd, server);
        cmd.Parameters.AddWithValue("$id", server.Id);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task DeleteAsync(ServerInfo server)
    {
        if (server.Id <= 0) return;
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);

        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Servers WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", server.Id);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>更新单个服务器的排序号。</summary>
    public async Task UpdateSortOrderAsync(int id, int sortOrder)
    {
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);

        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Servers SET SortOrder=$ord WHERE Id=$id";
        cmd.Parameters.AddWithValue("$ord", sortOrder);
        cmd.Parameters.AddWithValue("$id",  id);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>批量更新排序号。</summary>
    public async Task BatchUpdateSortOrderAsync(IEnumerable<(int Id, int SortOrder)> pairs)
    {
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);
        await using var txn = await conn.BeginTransactionAsync().ConfigureAwait(false);

        try
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Servers SET SortOrder=$ord WHERE Id=$id";

            foreach (var (id, ord) in pairs)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$ord", ord);
                cmd.Parameters.AddWithValue("$id",  id);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await txn.CommitAsync().ConfigureAwait(false);
        }
        catch
        {
            await txn.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>获取当前最大 SortOrder，用于新项追加。</summary>
    public async Task<int> GetMaxSortOrderAsync()
    {
        await EnsureAsync().ConfigureAwait(false);
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) FROM Servers";
        var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result is long l ? (int)l : -1;
    }

    private async Task EnsureAsync()
    {
        if (_initialized) return;

        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);

        var cmd = conn.CreateCommand();
        cmd.CommandText = CreateTableSql;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        await AddColumnIfMissingAsync(conn, "GroupName",         "TEXT DEFAULT ''");
        await AddColumnIfMissingAsync(conn, "SortOrder",         "INTEGER DEFAULT 0");
        await AddColumnIfMissingAsync(conn, "Protocol",          "TEXT DEFAULT 'RDP'");
        await AddColumnIfMissingAsync(conn, "SshPrivateKeyPath", "TEXT DEFAULT ''");
        await AddColumnIfMissingAsync(conn, "UseSocksProxy",      "INTEGER DEFAULT 0");
        await AddColumnIfMissingAsync(conn, "SocksProxyName",      "TEXT DEFAULT ''");
        await AddColumnIfMissingAsync(conn, "SocksProxyId",        "TEXT DEFAULT ''");

        _initialized = true;
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string columnName, string columnDef)
    {
        var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA table_info(Servers)";

        bool found = false;
        await using var reader = await pragmaCmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE Servers ADD COLUMN {columnName} {columnDef}";
            await alterCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            AppLogger.Info($"db migration: added column {columnName}");
        }
    }

    private SqliteConnection Open() => new("Data Source=" + _databasePath + ";Pooling=True");

    private static ServerInfo MapRow(SqliteDataReader r, string storedPw)
    {
        var protoStr = r.IsDBNull(9) ? "RDP" : r.GetString(9);
        var proto    = Enum.TryParse<ServerProtocol>(protoStr, out var p) ? p : ServerProtocol.RDP;
        string socksName = "";
        string socksId = "";
        if (r.FieldCount > 12 && !r.IsDBNull(12))
            socksName = r.GetString(12) ?? "";
        else if (r.FieldCount > 11 && !r.IsDBNull(11) && r.GetInt32(11) != 0)
            socksName = "默认";
        if (r.FieldCount > 13 && !r.IsDBNull(13))
            socksId = r.GetString(13) ?? "";
        return new()
        {
            Id                = r.GetInt32(0),
            Name              = r.IsDBNull(1) ? "" : r.GetString(1),
            IP                = r.IsDBNull(2) ? "" : r.GetString(2),
            Port              = r.IsDBNull(3) ? 3389 : r.GetInt32(3),
            Username          = r.IsDBNull(4) ? "" : r.GetString(4),
            Password          = CredentialProtector.Unprotect(storedPw),
            Description       = r.IsDBNull(6) ? "" : r.GetString(6),
            Group             = r.IsDBNull(7) ? "" : r.GetString(7),
            SortOrder         = r.IsDBNull(8) ? 0  : r.GetInt32(8),
            Protocol          = proto,
            SshPrivateKeyPath = r.IsDBNull(10) ? "" : r.GetString(10),
            SocksProxyId      = socksId,
            SocksProxyName    = socksName
        };
    }

    private static void BindParams(SqliteCommand cmd, ServerInfo s)
    {
        cmd.Parameters.AddWithValue("$n",       s.Name);
        cmd.Parameters.AddWithValue("$i",       s.IP);
        cmd.Parameters.AddWithValue("$p",       s.Port);
        cmd.Parameters.AddWithValue("$u",       s.Username          ?? "");
        cmd.Parameters.AddWithValue("$pw",      CredentialProtector.Protect(s.Password ?? ""));
        cmd.Parameters.AddWithValue("$desc",    s.Description       ?? "");
        cmd.Parameters.AddWithValue("$grp",     s.Group             ?? "");
        cmd.Parameters.AddWithValue("$ord",     s.SortOrder);
        cmd.Parameters.AddWithValue("$proto",   s.Protocol.ToString());
        cmd.Parameters.AddWithValue("$keyPath", s.SshPrivateKeyPath ?? "");
        cmd.Parameters.AddWithValue("$socks",   string.IsNullOrEmpty(s.SocksProxyId) && string.IsNullOrEmpty(s.SocksProxyName) ? 0 : 1);
        cmd.Parameters.AddWithValue("$socksName", s.SocksProxyName ?? "");
        cmd.Parameters.AddWithValue("$socksId", s.SocksProxyId ?? "");
    }

    // ── Port Forwards ──────────────────────────────────────────────────────────

    public async Task<List<PortForwardRule>> LoadPortForwardsAsync()
    {
        await EnsurePortForwardsTableAsync().ConfigureAwait(false);
        var result = new List<PortForwardRule>();
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id,Name,ServerId,ServerName,LocalPort,RemoteHost,RemotePort,ForwardType,AutoStart FROM PortForwards ORDER BY Id";
        await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await r.ReadAsync().ConfigureAwait(false))
        {
            var typeStr = r.IsDBNull(7) ? "Local" : r.GetString(7);
            var type = Enum.TryParse<PortForwardType>(typeStr, out var t) ? t : PortForwardType.Local;
            result.Add(new PortForwardRule
            {
                Id         = r.GetInt32(0),
                Name       = r.IsDBNull(1) ? "" : r.GetString(1),
                ServerId   = r.GetInt32(2),
                ServerName = r.IsDBNull(3) ? "" : r.GetString(3),
                LocalPort  = r.GetInt32(4),
                RemoteHost = r.IsDBNull(5) ? "127.0.0.1" : r.GetString(5),
                RemotePort = r.GetInt32(6),
                ForwardType= type,
                AutoStart  = r.GetInt32(8) != 0
            });
        }
        return result;
    }

    public async Task SavePortForwardAsync(PortForwardRule rule)
    {
        await EnsurePortForwardsTableAsync().ConfigureAwait(false);
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        if (rule.Id <= 0)
        {
            cmd.CommandText = "INSERT INTO PortForwards (Name,ServerId,ServerName,LocalPort,RemoteHost,RemotePort,ForwardType,AutoStart) VALUES ($n,$sid,$sname,$lp,$rh,$rp,$type,$auto); SELECT last_insert_rowid();";
            BindPortForwardParams(cmd, rule);
            rule.Id = (int)(long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
        }
        else
        {
            cmd.CommandText = "UPDATE PortForwards SET Name=$n,ServerId=$sid,ServerName=$sname,LocalPort=$lp,RemoteHost=$rh,RemotePort=$rp,ForwardType=$type,AutoStart=$auto WHERE Id=$id";
            BindPortForwardParams(cmd, rule);
            cmd.Parameters.AddWithValue("$id", rule.Id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    public async Task DeletePortForwardAsync(int id)
    {
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PortForwards WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task EnsurePortForwardsTableAsync()
    {
        if (_portForwardsInitialized) return;
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS PortForwards (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, ServerId INTEGER DEFAULT 0, ServerName TEXT DEFAULT '', LocalPort INTEGER DEFAULT 8080, RemoteHost TEXT DEFAULT '127.0.0.1', RemotePort INTEGER DEFAULT 80, ForwardType TEXT DEFAULT 'Local', AutoStart INTEGER DEFAULT 0)";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        _portForwardsInitialized = true;
    }

    private static void BindPortForwardParams(SqliteCommand cmd, PortForwardRule r)
    {
        cmd.Parameters.AddWithValue("$n",     r.Name);
        cmd.Parameters.AddWithValue("$sid",   r.ServerId);
        cmd.Parameters.AddWithValue("$sname", r.ServerName);
        cmd.Parameters.AddWithValue("$lp",    r.LocalPort);
        cmd.Parameters.AddWithValue("$rh",    r.RemoteHost);
        cmd.Parameters.AddWithValue("$rp",    r.RemotePort);
        cmd.Parameters.AddWithValue("$type",  r.ForwardType.ToString());
        cmd.Parameters.AddWithValue("$auto",  r.AutoStart ? 1 : 0);
    }

    // ── Quick Commands ──────────────────────────────────────────────────────────

    public async Task<List<QuickCommand>> LoadQuickCommandsAsync()
    {
        await EnsureQuickCommandsTableAsync().ConfigureAwait(false);
        var result = new List<QuickCommand>();
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id,Name,Command,GroupName,Description,SortOrder FROM QuickCommands ORDER BY SortOrder,Name";
        await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await r.ReadAsync().ConfigureAwait(false))
        {
            result.Add(new QuickCommand
            {
                Id          = r.GetInt32(0),
                Name        = r.IsDBNull(1) ? "" : r.GetString(1),
                Command     = r.IsDBNull(2) ? "" : r.GetString(2),
                Group       = r.IsDBNull(3) ? "默认" : r.GetString(3),
                Description = r.IsDBNull(4) ? "" : r.GetString(4),
                SortOrder   = r.GetInt32(5)
            });
        }
        return result;
    }

    public async Task SaveQuickCommandAsync(QuickCommand qc)
    {
        await EnsureQuickCommandsTableAsync().ConfigureAwait(false);
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        if (qc.Id <= 0)
        {
            cmd.CommandText = "INSERT INTO QuickCommands (Name,Command,GroupName,Description,SortOrder) VALUES ($n,$cmd,$grp,$desc,$ord); SELECT last_insert_rowid();";
            BindQcParams(cmd, qc);
            qc.Id = (int)(long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
        }
        else
        {
            cmd.CommandText = "UPDATE QuickCommands SET Name=$n,Command=$cmd,GroupName=$grp,Description=$desc,SortOrder=$ord WHERE Id=$id";
            BindQcParams(cmd, qc);
            cmd.Parameters.AddWithValue("$id", qc.Id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    public async Task DeleteQuickCommandAsync(int id)
    {
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM QuickCommands WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task EnsureQuickCommandsTableAsync()
    {
        if (_quickCommandsInitialized) return;
        await using var conn = Open();
        await conn.OpenAsync().ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS QuickCommands (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Command TEXT DEFAULT '', GroupName TEXT DEFAULT '默认', Description TEXT DEFAULT '', SortOrder INTEGER DEFAULT 0)";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        _quickCommandsInitialized = true;
    }

    private static void BindQcParams(SqliteCommand cmd, QuickCommand qc)
    {
        cmd.Parameters.AddWithValue("$n",    qc.Name);
        cmd.Parameters.AddWithValue("$cmd",  qc.Command);
        cmd.Parameters.AddWithValue("$grp",  qc.Group);
        cmd.Parameters.AddWithValue("$desc", qc.Description);
        cmd.Parameters.AddWithValue("$ord",  qc.SortOrder);
    }
}
