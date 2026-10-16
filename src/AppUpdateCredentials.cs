using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MoriDesk {
 // No token is embedded in the application or uploaded to Nook services.
 // DPAPI binds this local encrypted file to the current Windows account.
 internal sealed class AppUpdateCredentials {
  static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nook.GitHubUpdate.lta24131-hub.mori-desk.v1");
  readonly string root, file;
  internal AppUpdateCredentials() : this(Mori.Settings.ConfigDir) { }
  internal AppUpdateCredentials(string configRoot) {
   root = Path.Combine(AppUpdateService.SafeStagingRoot(configRoot), "update");
   file = Path.Combine(root, "github-token.dat");
  }
  internal bool HasStoredToken { get { return File.Exists(file); } }
  void Safe() {
   AppUpdateService.SafeStagingRoot(root);
   if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("GitHub 凭据文件不能是链接。");
  }
  internal string Load() {
   Safe(); if (!File.Exists(file)) return "";
   if (new FileInfo(file).Length > 16384) throw new InvalidDataException("本机 GitHub 凭据文件无效，请重新填写令牌。");
   byte[] plain = null;
   try {
    plain = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
    return AppUpdateService.NormalizeToken(Encoding.UTF8.GetString(plain));
   } catch (CryptographicException ex) { throw new InvalidDataException("无法解密这台电脑的 GitHub 凭据，请重新填写令牌。", ex); }
   finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
  }
  internal void Save(string token) {
   token = AppUpdateService.NormalizeToken(token); if (token.Length == 0) throw new ArgumentException("访问令牌为空，请填写令牌，或使用“清除凭据”。"); Safe();
   byte[] plain = Encoding.UTF8.GetBytes(token), encrypted;
   try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
   finally { Array.Clear(plain, 0, plain.Length); }
   Directory.CreateDirectory(root); Safe();
   string temporary = Path.Combine(root, ".github-token-" + Guid.NewGuid().ToString("N") + ".tmp");
   try {
    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(encrypted, 0, encrypted.Length); stream.Flush(true); }
    if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
   } finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }
  internal void Clear() { Safe(); if (File.Exists(file)) File.Delete(file); }
 }
}
