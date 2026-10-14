using System;
using System.IO;
using System.Collections.Generic;
namespace Mori {
 internal static class FormatPreviewInfo {
  static readonly HashSet<string> SystemImages=new HashSet<string>(new[]{".webp",".avif",".heic",".heif",".dng",".cr2",".cr3",".nef",".arw",".orf",".rw2",".raf"},StringComparer.OrdinalIgnoreCase);
  internal static bool UsesPagedPreview(string path){string ext=Path.GetExtension(path);return string.Equals(ext,".pdf",StringComparison.OrdinalIgnoreCase)||string.Equals(ext,".ai",StringComparison.OrdinalIgnoreCase);}
  internal static bool NeedsSystemImageCodec(string path){return SystemImages.Contains(Path.GetExtension(path));}
  internal static string Notice(string path){string ext=Path.GetExtension(path).ToLowerInvariant();return ext==".psd"?"PSD 合成画面 · 不编辑图层 · CMYK 为近似显示；复杂变体使用增强预览":ext==".psb"?"PSB 合成画面 · 不编辑图层 · 增强预览上限 256 MB / 3200 万像素":ext==".ai"?"AI 兼容 PDF 画面 · 缩略图 / 并排比较仅显示首页 · 不编辑画板":NeedsSystemImageCodec(path)?"优先 Windows 图片解码器；失败时使用内置增强编码组件，复杂 RAW / 变体可能仍需默认程序":"";}
 }
}
