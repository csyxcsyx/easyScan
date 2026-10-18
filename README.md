# easyScan

Windows 离线扫描工具：图片/PDF 校正、画质增强、中英文 OCR、TXT 与可搜索 PDF 导出。当前版本 **1.3.1**。

## 下载与使用

从 [GitHub Releases](https://github.com/csyxcsyx/easyScan/releases/latest) 下载 Windows x64 便携 ZIP，完整解压后运行 `easyScan.exe`，无需安装 .NET。OCR 模型已随包提供，运行时不联网。

1. 导入或拖入 JPG、PNG、HEIC/HEIF 或 PDF。
2. 检查纸张外缘，按需调整四角、弯边与增强参数。
3. 需要文字时点击「文字识别」；导出时选择图片/PDF，可同时保存 TXT 或添加可搜索文字层。

彩色适合证书、证件与印章；黑白适合普通文档。自动裁剪不确定时保留全图，仍需人工检查。详细操作、快捷键和功能限制见 [使用指南](docs/usage.md)。

## 源码结构

| 目录 | 内容 |
|---|---|
| `src/EasyScan.Core` | 解码、纸张检测、校正、增强、OCR、导出 |
| `src/EasyScan.Desktop` | WPF 工作台与交互 |
| `tests/EasyScan.Tests` | 图像、PDF、失败恢复、状态和界面回归 |
| `tools/EasyScan.Benchmarks` | 隔离的前后版本算法基准 |
| `scripts` | 模型/样本获取、验证与便携打包 |
| `docs` | 使用、开发、验证及论文方法说明 |
| `models/ocr`、`licenses` | 固定离线模型、校验记录及第三方许可 |

`bin`、`obj`、Python 缓存、`artifacts` 和 `dist` 均不提交；便携包通过 Releases 分发。

## 开发

需要 Windows x64 和 .NET 10 SDK。

```powershell
dotnet restore easyScan.sln --locked-mode
dotnet build easyScan.sln -c Release --no-restore
dotnet run --project tests/EasyScan.Tests -c Release --no-build -- artifacts/verification
powershell -ExecutionPolicy Bypass -File scripts/Publish.ps1
```

打包默认生成 `dist/easyScan-1.3.1-win-x64/`、同名 ZIP 及 SHA-256 校验文件，版本从项目文件读取。

- [开发与发布指南](docs/development.md)
- [验证结果与限制](docs/verification.md)
- [扫描算法与研究依据](docs/scan-algorithms.md)
- [更新记录](CHANGELOG.md)
- [第三方说明](THIRD-PARTY-NOTICES.md)
