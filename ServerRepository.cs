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
        "SocksProxyName TEXT DEFAULT '')";

    private const string SelectAllSql =
        "SELECT Id,Name,IP,Port,Username,Password,Description,GroupName,SortOrder,Protocol,SshPrivateKeyPath,UseSocksProxy,SocksProxyName " +
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
            "INSERT INTO Servers (Name,IP,Port,Username,Password,Description,GroupName,SortOrder,Protocol,SshPrivateKeyPath,UseSocksProxy,SocksProxyName) " +
            "VALUES ($n,$i,$p,$u,$pw,$desc,$grp,$ord,$proto,$keyPath,$socks,$socksName); SELECT last_insert_rowid();";
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
            "Description=$desc,GroupName=$grp,SortOrder=$ord,Protocol=$proto,SshPrivateKeyPath=$keyPath,UseSocksProxy=$socks,SocksProxyName=$socksName WHERE Id=$id";
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

    private SqliteConnection Open() => new("Data Source=" + _databasePath);

    private static ServerInfo MapRow(SqliteDataReader r, string storedPw)
    {
        var protoStr = r.IsDBNull(9) ? "RDP" : r.GetString(9);
        var proto    = Enum.TryParse<ServerProtocol>(protoStr, out var p) ? p : ServerProtocol.RDP;
        string socksName = "";
        if (r.FieldCount > 12 && !r.IsDBNull(12))
            socksName = r.GetString(12) ?? "";
        else if (r.FieldCount > 11 && !r.IsDBNull(11) && r.GetInt32(11) != 0)
            socksName = "默认";
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
        cmd.Parameters.AddWithValue("$socks",   string.IsNullOrEmpty(s.SocksProxyName) ? 0 : 1);
        cmd.Parameters.AddWithValue("$socksName", s.SocksProxyName ?? "");
    }
}
