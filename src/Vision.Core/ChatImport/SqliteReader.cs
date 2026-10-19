using System.Runtime.InteropServices;
using System.Text;

namespace Vision.Core;

/// <summary>Reads an SQLite database with the SQLite the system already has — Windows' own <c>winsqlite3.dll</c> (System32,
/// Windows 10 and later), <c>libsqlite3</c> on macOS and Linux — so no native package ships with Vision. Read-only: the file
/// is opened with SQLITE_OPEN_READONLY (and, when the live database's lock files can't be shared, as immutable), so a
/// program using it (OpenClaw) is never disturbed. Only what the chat import needs: prepare, bind text, step, read columns.</summary>
internal sealed unsafe class SqliteReader : IDisposable
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READONLY = 0x1;
    private const int SQLITE_OPEN_READWRITE = 0x2;
    private const int SQLITE_OPEN_CREATE = 0x4;
    private const int SQLITE_OPEN_URI = 0x40;
    private const int SQLITE_NULL = 5;

    private static readonly Lazy<Api?> Library = new(Api.Load);

    /// <summary>The system has an SQLite this reader can use.</summary>
    public static bool IsAvailable => Library.Value is not null;

    private readonly Api _api;
    private IntPtr _db;

    private SqliteReader(Api api, IntPtr db)
    {
        _api = api;
        _db = db;
    }

    /// <summary>Open <paramref name="path"/> read-only. Throws <see cref="IOException"/> with SQLite's reason when it can't.</summary>
    public static SqliteReader OpenReadOnly(string path)
    {
        var api = Library.Value ?? throw new IOException("This PC has no SQLite library Vision can use (winsqlite3.dll).");
        if (!File.Exists(path)) throw new FileNotFoundException("No such database.", path);
        // Read-only first: it sees what the running program wrote last (the -wal). When the lock files can't be opened (another
        // user's, or no -shm beside a -wal), immutable reads the main file as it is and touches nothing.
        if (TryOpen(api, path, SQLITE_OPEN_READONLY, out var db, out var first)) return new SqliteReader(api, db);
        if (TryOpen(api, ImmutableUri(path), SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, out db, out _)) return new SqliteReader(api, db);
        throw new IOException("Couldn't open the database: " + first);
    }

    /// <summary>For tests: create (or open) a database to write a fixture into.</summary>
    internal static SqliteReader OpenForWriting(string path)
    {
        var api = Library.Value ?? throw new IOException("No SQLite library.");
        if (TryOpen(api, path, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, out var db, out var reason)) return new SqliteReader(api, db);
        throw new IOException(reason);
    }

    private static bool TryOpen(Api api, string name, int flags, out IntPtr db, out string reason)
    {
        var bytes = Utf8(name);
        int rc;
        fixed (byte* p = bytes)
        {
            IntPtr handle;
            rc = api.Open(p, &handle, flags, IntPtr.Zero);
            db = handle;
        }
        if (rc == SQLITE_OK)
        {
            api.BusyTimeout(db, 3000);
            reason = "";
            return true;
        }
        reason = db == IntPtr.Zero ? $"error {rc}" : Message(api, db);
        if (db != IntPtr.Zero) api.Close(db);
        db = IntPtr.Zero;
        return false;
    }

    /// <summary>"file:///C:/Users/pat/x.sqlite?immutable=1" (a path as an SQLite URI).</summary>
    internal static string ImmutableUri(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        var escaped = new StringBuilder();
        foreach (var c in full)
        {
            if (c is '%' or '?' or '#' or ' ') escaped.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            else escaped.Append(c);
        }
        var text = escaped.ToString();
        return "file:" + (text.StartsWith('/') ? "//" : "///") + text + "?immutable=1";
    }

    /// <summary>Run a query and hand each row to <paramref name="row"/>. Parameters are bound as text (null as NULL).</summary>
    public void Query(string sql, Action<Row> row, params string?[] parameters)
    {
        if (_db == IntPtr.Zero) throw new ObjectDisposedException(nameof(SqliteReader));
        IntPtr stmt;
        var bytes = Utf8(sql);
        fixed (byte* p = bytes)
        {
            IntPtr handle;
            var rc = _api.Prepare(_db, p, -1, &handle, IntPtr.Zero);
            if (rc != SQLITE_OK) throw new IOException(Message(_api, _db));
            stmt = handle;
        }
        try
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i] is not { } value)
                {
                    _api.BindNull(stmt, i + 1);
                    continue;
                }
                var data = Encoding.UTF8.GetBytes(value);
                fixed (byte* v = data)
                    _api.BindText(stmt, i + 1, v, data.Length, new IntPtr(-1)); // SQLITE_TRANSIENT: SQLite copies it
            }
            var current = new Row(_api, stmt);
            while (true)
            {
                var rc = _api.Step(stmt);
                if (rc == SQLITE_ROW) row(current);
                else if (rc == SQLITE_DONE) break;
                else throw new IOException(Message(_api, _db));
            }
        }
        finally
        {
            _api.FinalizeStatement(stmt);
        }
    }

    /// <summary>For tests: run statements that return nothing.</summary>
    internal void Execute(string sql, params string?[] parameters) => Query(sql, _ => { }, parameters);

    /// <summary>Whether a table exists.</summary>
    public bool HasTable(string name)
    {
        var found = false;
        Query("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?", _ => found = true, name);
        return found;
    }

    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        _api.Close(_db);
        _db = IntPtr.Zero;
    }

    private static string Message(Api api, IntPtr db) => Marshal.PtrToStringUTF8(api.ErrorMessage(db)) ?? "unknown error";

    private static byte[] Utf8(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    /// <summary>The current row of a query.</summary>
    public readonly struct Row
    {
        private readonly Api _api;
        private readonly IntPtr _stmt;

        internal Row(Api api, IntPtr stmt)
        {
            _api = api;
            _stmt = stmt;
        }

        public string? Text(int column)
        {
            if (_api.ColumnType(_stmt, column) == SQLITE_NULL) return null;
            var p = _api.ColumnText(_stmt, column);
            if (p == null) return null;
            var length = _api.ColumnBytes(_stmt, column);
            return Encoding.UTF8.GetString(p, length);
        }

        public long? Int(int column) => _api.ColumnType(_stmt, column) == SQLITE_NULL ? null : _api.ColumnInt64(_stmt, column);
    }

    /// <summary>The handful of SQLite functions used, resolved once from the system library.</summary>
    internal sealed class Api
    {
        public delegate* unmanaged[Cdecl]<byte*, IntPtr*, int, IntPtr, int> Open;
        public delegate* unmanaged[Cdecl]<IntPtr, int> Close;
        public delegate* unmanaged[Cdecl]<IntPtr, int, int> BusyTimeout;
        public delegate* unmanaged[Cdecl]<IntPtr, byte*, int, IntPtr*, IntPtr, int> Prepare;
        public delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, IntPtr, int> BindText;
        public delegate* unmanaged[Cdecl]<IntPtr, int, int> BindNull;
        public delegate* unmanaged[Cdecl]<IntPtr, int> Step;
        public delegate* unmanaged[Cdecl]<IntPtr, int, int> ColumnType;
        public delegate* unmanaged[Cdecl]<IntPtr, int, byte*> ColumnText;
        public delegate* unmanaged[Cdecl]<IntPtr, int, int> ColumnBytes;
        public delegate* unmanaged[Cdecl]<IntPtr, int, long> ColumnInt64;
        public delegate* unmanaged[Cdecl]<IntPtr, int> FinalizeStatement;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr> ErrorMessage;

        private static readonly string[] Names = OperatingSystem.IsWindows()
            ? ["winsqlite3.dll", "sqlite3.dll"]
            : OperatingSystem.IsMacOS()
                ? ["/usr/lib/libsqlite3.dylib", "libsqlite3.dylib"]
                : ["libsqlite3.so.0", "libsqlite3.so"];

        public static Api? Load()
        {
            foreach (var name in Names)
            {
                if (!NativeLibrary.TryLoad(name, out var library)) continue;
                try
                {
                    return new Api
                    {
                        Open = (delegate* unmanaged[Cdecl]<byte*, IntPtr*, int, IntPtr, int>)NativeLibrary.GetExport(library, "sqlite3_open_v2"),
                        Close = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(library, "sqlite3_close_v2"),
                        BusyTimeout = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(library, "sqlite3_busy_timeout"),
                        Prepare = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, IntPtr*, IntPtr, int>)NativeLibrary.GetExport(library, "sqlite3_prepare_v2"),
                        BindText = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, IntPtr, int>)NativeLibrary.GetExport(library, "sqlite3_bind_text"),
                        BindNull = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(library, "sqlite3_bind_null"),
                        Step = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(library, "sqlite3_step"),
                        ColumnType = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(library, "sqlite3_column_type"),
                        ColumnText = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*>)NativeLibrary.GetExport(library, "sqlite3_column_text"),
                        ColumnBytes = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)NativeLibrary.GetExport(library, "sqlite3_column_bytes"),
                        ColumnInt64 = (delegate* unmanaged[Cdecl]<IntPtr, int, long>)NativeLibrary.GetExport(library, "sqlite3_column_int64"),
                        FinalizeStatement = (delegate* unmanaged[Cdecl]<IntPtr, int>)NativeLibrary.GetExport(library, "sqlite3_finalize"),
                        ErrorMessage = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)NativeLibrary.GetExport(library, "sqlite3_errmsg"),
                    };
                }
                catch (EntryPointNotFoundException)
                {
                    NativeLibrary.Free(library);
                }
            }
            return null;
        }
    }
}
