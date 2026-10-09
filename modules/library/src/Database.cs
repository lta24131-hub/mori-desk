using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Mori {
    // Windows' maintained SQLite runtime; no bundled database engine.
    public sealed class Database : IDisposable {
        IntPtr db;
        readonly object gate = new object();
        int transactionDepth;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Callback(IntPtr p, int count, IntPtr values, IntPtr names);
        [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Unicode)] static extern int sqlite3_open16(string file, out IntPtr handle);
        [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr handle, byte[] sql, Callback cb, IntPtr arg, out IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern void sqlite3_free(IntPtr p);
        [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr handle);
        static string Utf8(IntPtr p) { if (p == IntPtr.Zero) return ""; int n=0; while (Marshal.ReadByte(p,n)!=0) n++; byte[] b=new byte[n]; Marshal.Copy(p,b,0,n); return Encoding.UTF8.GetString(b); }
        public static string Q(string s) { return "'"+(s??"").Replace("\0", "").Replace("'", "''")+"'"; }
        public Database(string path) {
            if (sqlite3_open16(path, out db)!=0) { if (db!=IntPtr.Zero) sqlite3_close(db); throw new Exception("无法打开图库索引："+path); }
            Exec("PRAGMA busy_timeout=10000; PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
        }
        public List<Dictionary<string,string>> Query(string sql) {
            lock(gate) {
                var rows=new List<Dictionary<string,string>>();
                Callback cb=delegate(IntPtr p,int n,IntPtr vals,IntPtr names) { var row=new Dictionary<string,string>(); for(int i=0;i<n;i++) row[Utf8(Marshal.ReadIntPtr(names,i*IntPtr.Size))]=Utf8(Marshal.ReadIntPtr(vals,i*IntPtr.Size)); rows.Add(row); return 0; };
                IntPtr err; int rc=sqlite3_exec(db,Encoding.UTF8.GetBytes(sql+"\0"),cb,IntPtr.Zero,out err);
                if(rc!=0) { string detail=Utf8(err); sqlite3_free(err); throw new Exception("图库索引错误："+detail); }
                return rows;
            }
        }
        public void Exec(string sql) { Query(sql); }
        public void Transaction(Action work) { lock(gate) {
            bool outer=transactionDepth==0;string savepoint="mori_nested_"+transactionDepth;
            Exec(outer?"BEGIN IMMEDIATE":"SAVEPOINT "+savepoint);transactionDepth++;
            try { work(); Exec(outer?"COMMIT":"RELEASE "+savepoint); }
            catch { Exec(outer?"ROLLBACK":"ROLLBACK TO "+savepoint+"; RELEASE "+savepoint); throw; }
            finally { transactionDepth--; }
        } }
        public void Dispose() { lock(gate) { if(db!=IntPtr.Zero) { sqlite3_close(db); db=IntPtr.Zero; } } }
    }
}
