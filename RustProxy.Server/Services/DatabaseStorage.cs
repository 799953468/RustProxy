using Microsoft.Data.Sqlite;
using RustProxy.Core.Storage;

namespace RustProxy.Server.Services;

/// <summary>Separates metadata from resource objects and preserves the original legacy database during migration.</summary>
public static class DatabaseStorage
{
    /// <summary>Migrates optional columns in existing SQLite tables while retaining columns, indexes and triggers.</summary>
    public static void UpgradeOptionalColumns(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        MakeNullable(connection, "Packages", "Source");
        MakeNullable(connection, "PackageCargoDependencies", "Checksum");
    }

    private static void MakeNullable(SqliteConnection connection, string table, string optionalColumn)
    {
        var columns = new List<(string Name, string Type, bool Required, string? Default, int Primary)>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = $"PRAGMA table_info({Quote(table)})";
            using var reader = query.ExecuteReader();
            while (reader.Read()) columns.Add((reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5)));
        }
        if (!columns.Any(x => x.Name == optionalColumn && x.Required)) return;
        var recreate = new List<string>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT sql FROM sqlite_master WHERE tbl_name = $table AND type IN ('index', 'trigger') AND sql IS NOT NULL";
            query.Parameters.AddWithValue("$table", table);
            using var reader = query.ExecuteReader();
            while (reader.Read()) recreate.Add(reader.GetString(0));
        }
        var temporary = table + "_nullable_" + Guid.NewGuid().ToString("N");
        var definitions = columns.Select(x => Quote(x.Name) + " " + x.Type + (x.Required && x.Name != optionalColumn ? " NOT NULL" : "") + (x.Default is null ? "" : " DEFAULT " + x.Default)).ToList();
        var primary = columns.Where(x => x.Primary > 0).OrderBy(x => x.Primary).Select(x => Quote(x.Name)).ToArray();
        if (primary.Length > 0) definitions.Add("PRIMARY KEY (" + string.Join(",", primary) + ")");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"CREATE TABLE {Quote(temporary)} ({string.Join(",", definitions)})";
        command.ExecuteNonQuery();
        var names = string.Join(",", columns.Select(x => Quote(x.Name)));
        command.CommandText = $"INSERT INTO {Quote(temporary)} ({names}) SELECT {names} FROM {Quote(table)}";
        command.ExecuteNonQuery();
        command.CommandText = $"DROP TABLE {Quote(table)}";
        command.ExecuteNonQuery();
        command.CommandText = $"ALTER TABLE {Quote(temporary)} RENAME TO {Quote(table)}";
        command.ExecuteNonQuery();
        foreach (var sql in recreate) { command.CommandText = sql; command.ExecuteNonQuery(); }
        transaction.Commit();
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>Returns the private SQLite path, taking a consistent SQLite backup of a legacy database when needed.</summary>
    public static string Prepare(string contentRoot)
    {
        var root = Path.Combine(contentRoot, "data");
        PathSafety.RejectLink(root);
        Directory.CreateDirectory(root);
        var target = PathSafety.Resolve(root, "metadata/rustproxy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var legacy = PathSafety.Resolve(root, "rustproxy.db");
        if (!File.Exists(target) && File.Exists(legacy))
        {
            var temporary = target + ".migration-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacy, Mode = SqliteOpenMode.ReadOnly }.ToString()))
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary }.ToString()))
                {
                    source.Open();
                    destination.Open();
                    source.BackupDatabase(destination);
                }
                SqliteConnection.ClearAllPools();
                File.Move(temporary, target);
            }
            finally { PackageStore.DeleteTemporary(temporary); }
        }
        return target;
    }
}
