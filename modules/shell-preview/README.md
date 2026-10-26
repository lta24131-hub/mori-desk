# Windows 文件缩略图与格式图标

资源管理器优先显示文件内容缩略图。Nook 保留 Windows 和其他软件已有的图片、
视频及文档缩略图处理器。关联为由 Nook 打开的文件没有内容预览时，由独立的格式
图标显示 SVG、PNG、PDF 等后缀；其他默认应用保留原图标。小图标模式使用类别符号。

本组件新增 SVG 内容缩略图。内含外部资源、脚本、内嵌图片、动画或超过资源预算
的 SVG 暂退回格式图标，不修改原文件。它不会让 Windows 原本不支持的所有视频
编码、PDF、PSD 或 AI 自动拥有缩略图，也不会更改用户“始终显示图标”的设置。

仅正常安装版在更新提交并确认启动后启用；便携、测试和隔离数据窗口不注册。
不修改 UserChoice，不强制切换默认打开方式，不覆盖其他缩略图插件，不清空系统
缩略图缓存或重启 Explorer。Windows 缓存可能在打开文件夹或正常刷新后才更新。

原生组件使用不可变的 `Launcher/Shell/<manifest-sha256>` 目录，避免 Explorer
加载 DLL 后妨碍主程序旧版本清理。组件更新只改变注册指向，不删除正在使用的 DLL。

## 构建

1. 用 `tools/Build-FileIcons.ps1` 重建图标（Windows PowerShell 5）。
2. 准备 Zig 0.14.1，以及 `resvg 0.49.0` 官方 Windows x64 文件和 MIT/Apache
   许可文本；`Build-Component.ps1` 核对固定输入哈希。
3. 调用 `Build-Component.ps1 -Compiler <zig.exe> -RendererDirectory <resvg-folder>
   -OutputDirectory <fresh-output>`，仅编译，不注册或运行图标处理器。
4. 主构建传 `-ShellPreviewDirectory <fresh-output>`；发行打包要求完整组件清单。

## 验证边界

原生测试程序与独立注册表测试只在开发虚拟机执行。真实 Shell 验收必须使用
`IShellItemImageFactory` 的 `SIIGBF_THUMBNAILONLY`，以内容像素证明预览有效，
不能把图标回退当成缩略图成功。还需要验证既有 PNG/JPEG/视频预览、默认应用选择、
“打开方式”的 Nook 应用图标、异常输入和升级后的组件路径。测试结果单独保存，
构建成功不代表这些项目已验收。
