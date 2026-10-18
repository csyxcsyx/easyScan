# 开发与发布

源码开发需要 Windows 10/11 x64 和 .NET 10 SDK。`global.json` 选择 10.0.202，并允许同一主版本的后续功能更新。运行便携包不需要 SDK、Python 或单独安装 .NET。

## 构建与回归

```powershell
dotnet restore easyScan.sln --locked-mode
dotnet build easyScan.sln -c Release --no-restore
dotnet run --project src/EasyScan.Desktop -c Release --no-build
dotnet run --project tests/EasyScan.Tests -c Release --no-build -- artifacts/verification
```

测试是 WPF 回归执行程序，必须使用 `dotnet run`，不是 `dotnet test`。产物目录后的参数可选择 `--research-only`、`--scan-only`、`--scene-only`、`--export-only` 或 `--workflow-only`，也可传入本地图片路径。所有图像、日志和临时输出均位于被忽略的 `artifacts`。

独立 PDF 文本和坐标检查需要测试环境中的 `pypdf`：

```powershell
python -m pip install pypdf
python scripts/VerifyOcrPdf.py artifacts/verification/扫描优化/pdf-checks.json
```

原生运行诊断无需打开工作台，涵盖解码、颜色转换、中文水印、PDF 回读、离线 OCR 和可搜索 PDF/TXT：

```powershell
dotnet src/EasyScan.Desktop/bin/Release/net10.0-windows/easyScan.dll --verify-runtime artifacts/runtime
```

## 算法基准

`tools/EasyScan.Benchmarks` 已纳入解决方案，使用独立程序集加载上下文对比保存的基线 DLL 与当前版本，避免把同一个程序集误作前后对照。先完成全量回归以生成固定样本，再按需获取公开研究样本：

```powershell
python scripts/FetchResearchSamples.py
powershell -ExecutionPolicy Bypass -File scripts/RunResearchBenchmarks.ps1 -BaselineCore artifacts/research-baseline/EasyScan.Core.dll -Fixtures artifacts/verification
```

基线 DLL 必须从修改前的构建保留，仓库不分发历史二进制或真实照片。来源、校验与许可写入下载 manifest。指标解释与限制见 [扫描算法](scan-algorithms.md)。

## 依赖与模型

各项目通过 `packages.lock.json` 固定 NuGet 依赖。更新依赖时重新生成锁文件，并执行全量回归和原生运行诊断。

OCR 固定使用 RapidOCR v3.10.0 分发的 PP-OCRv5 mobile 资产，约 22.5 MB，来源和 SHA-256 见 `models/ocr/manifest.json`。文件丢失可执行 `python scripts/FetchOcrModels.py`，运行时不会下载。打包脚本同时校验来源和便携目录中的模型。

PDFsharp 固定 MIT 版本 6.2.4，许可和模型声明见根目录 [第三方说明](../THIRD-PARTY-NOTICES.md)。不要在整理时删除第三方许可、字典或内嵌 PDF 字体。

## 本地打包与 GitHub Release

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Publish.ps1
```

脚本从 Desktop 项目的 `Version` 读取版本，在 `dist/easyScan-<版本>-win-x64` 生成自包含程序，并附文档、许可、固定模型、构建提交信息、ZIP 和 `.zip.sha256`。可用 `-OutputDirectory` 指定其他目录。

发布前先提交代码，使构建信息对应确定的源码提交；在输出目录运行 `easyScan.exe --verify-runtime <诊断输出目录>`，检查 ZIP 和模型校验值，然后将版本标签、ZIP 与校验文件发布到 GitHub Releases。Release 说明使用简短中文，记录本版变化、运行平台和已知限制。
