# easyScan 1.3 扫描优化与离线 OCR 验证

日期：2026-10-15。Windows x64，.NET SDK 10.0.202 / 运行时 10.0.6。

## 本次结果

- Release 构建 **0 错误、0 警告**；完整自动化 **232 项断言通过**，包括原有 187 项和新增 45 项。日志：`artifacts/scan-optimization-final/verification.log`。
- 新增线段及 Lab 色差候选、四边支持度与竞争候选判断，直边在最长边不超过 2400 像素的图像上精修；弯边仍保留曲线模型。2600×1800 合成透视样本的最差角点误差为短边的 **0.0492%**。原有浅色桌面、纹理、低光、弯边、小证件及装饰内框回归继续通过，背景混入均低于 2.5%。
- 多尺度纸张背景估计、可调保边降噪和受限细节增强已接入，自然模式继续保护印章色相。特定平坦纸张噪声样本标准差 **6.068 → 1.969**；原有曝光、阴影、红章色相和饱和度检查通过。
- 用与旧版相同的 Gaussian/Sauvola 路径作对照：3200 像素宽、155 灰度的一像素细线真值样本，前景 F1 **0 → 1**，新输出未引入周围背景墨迹。这是极细笔画的针对性测试，不代表任意真实文档都能达到该提升。
- 固定五行印刷中英混排、数字和浅色文字样本，字符错误率为 **0%**；强阴影输入和增强后也都为 0%。因此该样本证明识别和增强兼容，不能据此宣称真实材料错误率普遍下降。原图、阴影图和增强图均在 `artifacts/scan-optimization-final/扫描优化/`。
- OCR 不随导入自动运行；当前/选中/全部识别、取消后继续识别、结果复用、参数及源文件失效、处理中源文件变化、模型缺失/损坏和部分页失败均通过。识别结果独立于扫描状态；批量取消保留已完成结果。
- 1600/3200 像素预览分别缓存。高清命中复用位图；即使单个当前页面大于人为设置的小预算，缓存仍会淘汰该条目并能再次生成。正常会话清理及原有缓存回归通过。
- 彩色、黑白、灰度 PDF 覆盖每页一张、连续排版、横向白纸，以及旋转与弯边校正。启用隐藏文字层前后，PDF 回读渲染 **逐像素相同**。可搜索 PDF 与同名 UTF-8 TXT 导出通过；OCR 失败时不保存缺少文字层或缺页的 PDF。
- 使用独立的 **pypdf** 解析器核对 3 份 PDF、5 张纸、342 个字符：中文/英文/数字文本完整，文字均为不可见渲染模式，所有字符坐标与扫描图排版对应；最大数值误差 **0.000410 pt**。记录：`扫描优化/pdf-independent-report.json`。
- 已检查 OCR 结果窗口、窄工作台及导出窗口的 WPF 渲染，覆盖 96/144/192 DPI；新增界面绑定诊断无错误。右侧 OCR 和清晰度设置位于可滚动处理区，导出保存位置仍固定显示。
- 本地 Release 程序的无窗口原生诊断通过，包含离线 OCR、可搜索 PDF/TXT、图像/PDF 回读和中文水印；运行记录：`artifacts/scan-optimization-runtime/runtime-report.json`。该诊断使用本机 .NET 10.0.6，并非另一台无开发环境电脑上的便携包验收。

## 资源与性能

- PP-OCRv5 mobile 模型和字典约 **22.5 MB**，固定为 RapidOCR v3.10.0 分发文件，四个 SHA-256 均已校验。`models/ocr/manifest.json` 记录来源与哈希；运行时不访问网络。
- 新增主要 Windows x64 OCR 文件按唯一文件计约 **39.1 MB（未压缩）**：模型、字典、ONNX Runtime 原生/托管库和 Clipper2。此数值不等同于 ZIP 包实际增量；本次没有生成或发布新的便携包。
- 本机一次 Release 验证中，2600×1800 样本检测约 **414 ms**，1200×800 五行文字的模型加载及首次 OCR 约 **710 ms**。完整测试进程（包含 WPF 渲染及多个服务）的峰值工作集约 **755 MiB**。这些是单机单次记录，不是普通设备的耗时或常驻内存保证。
- 锁定 ONNX Runtime 1.29.0、Clipper2 2.0.0，复用现有 OpenCV 和 SkiaSharp 4.150.1，未引入冲突的 SkiaSharp 3。依赖锁、模型许可和原生第三方通知已更新；PDF 的 GlyphLessFont 内嵌，来源及许可见 `licenses/OCR-NOTICE.txt`。

## 限制与重现

本轮未提供原始证书/证件照片，新增画质和 OCR 数值来自带真值的合成材料，不能替代真实照片验收。未验证专门的环形印章 OCR、手写、表格结构还原、严重书页展平或百页压力负载。UI 检查依靠 WPF 渲染和控件/命令测试，没有完成真实桌面的持续鼠标操作、完整辅助技术遍历或实际 PDF 阅读器中的手动选词。Windows 10、Windows N、ARM64 及另一台干净电脑仍未验收。

~~~powershell
dotnet restore easyScan.sln --locked-mode
dotnet build easyScan.sln -c Release --no-restore
dotnet run -c Release --no-build --project tests/EasyScan.Tests -- artifacts/scan-optimization-final
dotnet run -c Release --no-build --project tests/EasyScan.Tests -- artifacts/scan-optimization --scan-only
# 以下独立解析需要测试环境安装 pypdf，不是应用运行依赖。
python scripts/VerifyOcrPdf.py artifacts/scan-optimization-final/扫描优化/pdf-checks.json
dotnet src/EasyScan.Desktop/bin/Release/net10.0-windows/easyScan.dll --verify-runtime artifacts/scan-optimization-runtime
~~~

---

# easyScan 1.2 验证记录（历史）

日期：2026-10-15。环境：Windows x64，24H2（build 26100），.NET SDK 10.0.202；便携版包含 .NET 10.0.6。

## 已验证

- 完整自动化运行 **189 项断言通过**，覆盖原有裁剪、增强、导入和导出回归，以及本次预览、缓存、排版、水印和界面状态。日志和快照位于 artifacts/v1.2/。WPF 绑定诊断无错误，Debug 构建及 Release 自包含发布无错误、无警告。
- 原图、结果、对比互斥；从对比切换原图有效。裁剪取消恢复此前视图，确认回到结果。对比两侧联动缩放和平移，鼠标锚点保持，倍率边界正确禁用；裁剪期间不提供无效的预览缩放。
- 导入只为每页生成一次处理预览；切回已查看页面绑定同一张冻结位图，不重新增强。在固定两页合成测试中，缓存返回约 3～4 ms，不是所有设备或工作负载的性能保证。
- 模拟内存淘汰后从磁盘恢复预览，不重新解码原文件或增强；颜色转换复用彩色主预览。参数只使对应页重算，源文件版本变化正确失效，延迟旧请求不能覆盖新选择。正常关闭清理会话缓存，不删除非缓存目录。
- 专业词汇的帮助提示验证了延迟打开、同一浮层立即替换、离开后短暂保留及自动关闭。折叠标题展开后不保留按压底色；下拉菜单具有不透明背景和独立阴影。
- 实际 WPF 渲染覆盖宽窄工作台、对比与放大对比、八点裁剪、展开参数、加载反馈、帮助浮层、下拉菜单、图片/白纸/水印导出及失败恢复。96/144/192 DPI 快照已检查。保存位置固定在底部，文本框重复内边距造成的文字裁切已修正并加入检查。
- PDF 回读验证每页一张与连续排版的页数、统一 A4 尺寸、比例、留白、不拆开单图和横向尺寸。固定 800×500 的六张扫描件，连续排版输出三张 A4 纸；每页一张输出六页。预览与导出使用相同排版器。
- 中文及英文水印、斜向居中/平铺/角落署名、彩色/灰度/黑白、空文字验证、扫描图/整页范围和取消均通过。水印 PNG 与同分辨率合成结果逐像素一致，彩色主结果保持不变；黑白 PNG 使用点阵透明度，仍只包含 0/255。
- 图片导出明确选择 JPG 或 PNG，PNG 隐藏 JPG 质量；导出范围改变会重建预览页；从合并 PDF 切回自动正确取消不兼容的组织方式。
- 继续验证了多页 PDF、来源分组、中文路径、同名不覆盖、加密文件拒绝、失败隔离和取消保留已完成结果。合并 PDF 有失败内容时不静默保存缺页版本。
- 两张参考证书实际输出复查：JPG 保留外缘和印章；4032×3024 HEIC 保留全图并标记待确认。裁剪及增强算法本次未变更，原有明暗、反光、偏色、弯边和小证件回归继续通过。

## 原生运行与分发

便携 exe 已通过原生诊断：加载包内 .NET 10.0.6，HEIC/JPG/弯边样本解码，12 张彩色/灰度/黑白图片输出，四页 PDF 回读渲染，中文水印 PNG，以及带整页水印的连续排版三页 PDF。退出码 0；最终运行记录保留在 artifacts/v1.2-final-runtime/runtime-report.json。

发布脚本保持 UTF-8 BOM，兼容 Windows PowerShell 的中文使用说明文件名；移除未使用的视频插件和原生调试符号，保留运行时、原生图像组件与许可文件。参考照片、缓存、测试输出和界面快照不随便携包分发。

## 未验证与限制

- UI 验证依靠应用自身 WPF 渲染、控件事件和模型检查；未完成真实桌面的持续鼠标拖动、完整 Tab/Shift+Tab 遍历、文件对话框、屏幕阅读器及所有真实悬停/按压/释放状态操作。
- 加载动画代码遵循系统关闭动画设置，但尚未实际切换系统动画或高对比度。DPI 快照不等同于真实显示器间移动和系统缩放切换。
- 尚未在无开发环境的另一台电脑、Windows 10、Windows N 或 ARM64 上验收；目标为 Windows 10/11 x64。
- 尚未进行百页以上压力测试。预览最长边 1600 像素，深度放大会模糊；导出使用原分辨率。临时缓存超过预算后被淘汰的旧页可能需要重新处理。
- 严重折皱、同色复杂背景、遮挡及完全过曝或已丢失的内容仍有原有处理限制。JPG 压缩会产生过渡灰色；严格二值结果请选择 PNG。
- 中文水印依赖系统微软雅黑，字体不随包分发；字体缺失会明确报错，可关闭水印继续使用。

## 重现

~~~powershell
dotnet run --project tests/EasyScan.Tests -- artifacts/v1.2 "本地参考照片.jpg" "本地参考照片.heic"
dotnet run --project tests/EasyScan.Tests -- artifacts/v1.2 --export-only
dotnet run --project tests/EasyScan.Tests -- artifacts/v1.2 --workflow-only
powershell -ExecutionPolicy Bypass -File scripts/Publish.ps1
~~~

便携包诊断（无窗口，会在指定目录生成测试图片和 PDF）：

~~~powershell
.\easyScan.exe --verify-runtime "本机诊断输出目录" "可选 HEIC 路径" "可选弯边图片路径"
~~~

PDFsharp 保持 MIT 版本 6.2.4，Pkcs 直接锁定 10.0.13，其余依赖保持现有锁定版本。本次未增加第三方依赖。完整版本和许可见 packages.lock.json 与 THIRD-PARTY-NOTICES.md。


## 2026-10-15 第二轮研究方法优化

本轮完整回归为 **246 项断言通过**，Release 零警告、零错误；独立 PDF 检查 3 个文件通过。原有 232 项全部保留。质量、速度、内存与批量稳定性检查由 `scripts/VerifyResearchBenchmarks.py` 验证通过，详细条件、真实/合成结果和限制见 [RESEARCH_ALGORITHMS.md](RESEARCH_ALGORITHMS.md)。

主验证目录为 `artifacts/research-final/`；逐项前后基准为 `artifacts/research-bench/`。公开数据校验值在 `artifacts/research-data/manifest.json`。未重新发布便携包。
