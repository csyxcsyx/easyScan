# 扫描算法研究与验证记录（2026-10-15）

本轮实现采用近年文档处理研究中的设计原则，保持 Windows x64、离线 CPU 和原有模型。Release 构建零警告、零错误；完整回归 **246 项断言通过**（原 232 项全部保留），另有独立 PDF 和研究基准检查。没有创建 commit、发布或更新 dist 便携包。

## 研究来源与实际采用范围

| 来源 | 实际实现 | 未复现的部分 |
|---|---|---|
| [Document Image Shadow Removal Guided by Color-Aware Background，CVPR 2023](https://openaccess.thecvf.com/content/CVPR2023/html/Zhang_Document_Image_Shadow_Removal_Guided_by_Color-Aware_Background_CVPR_2023_paper.html) | 空间变化的背景颜色；在低分辨率上按色相和饱和度统计可信纸张亮度，让不同纸色拥有不同亮度参考。 | CBENet、BGShadowNet、注意力网络及论文权重。这里是确定性的 OpenCV 工程改编，不是原网络复现。 |
| [High-Resolution Document Shadow Removal … Frequency-Aware Shadow Erasing Net，ICCV 2023](https://openaccess.thecvf.com/content/ICCV2023/html/Li_High-Resolution_Document_Shadow_Removal_via_A_Large-Scale_Real-World_Dataset_and_ICCV_2023_paper.html) | 借鉴低频校正与高频细节分开处理的原则：小图估计平滑增益，原分辨率应用，避免先把文字缩小再重建。 | FSENet 的频域网络、训练损失和权重；不将普通平滑与乘法校正称为 FSENet 实现。 |
| [PaddleOCR 3.0 Technical Report，2025](https://arxiv.org/html/2507.05595v1)（技术报告/预印本） | 保留报告对应的 PP-OCRv5 mobile 检测与识别路线，核对模型维度、输入和字典；优化运行时批处理、方向处理、长行采样。 | 不重新训练模型，不使用报告默认 server 版的精度或速度作为 mobile 版承诺。 |

经典 Sauvola 阈值与受限锐化继续作为稳定基础。新实现没有增加大型修复模型、Python 运行时或运行时下载，也没有复制上述论文项目的网络代码。不能将“借鉴设计原则”解释为继承其榜单成绩。

## 实现与兼容

- `DocumentIllumination`：最长边 512 像素估计；形态学背景排除暗笔画，再用可信背景的归一化局部统计重建中性色纸张。根据 HSV 色组的高分位亮度建立局部参考；HSV 在近似中性乘性阴影下比直接量化 Lab 色度更稳定。稀少颜色不被当作独立纸张，可信背景不足 15% 时退回原背景估计。
- 照明校正保留原分辨率像素，以平滑小图增益处理亮度，增益限制沿用 0.68–2.15。使用条带仿射插值，避免逐像素生成大幅坐标图；继续应用现有降噪、受限锐化和印章色相保护。
- Sauvola 使用 1024 行条带及半窗口重叠，字节降噪图只计算一次。三个浮点统计缓冲区复用，直接箱式与平方箱式滤波从字节图计算均值和均方值，阈值公式、边界语义和原灰度比较保持一致。新增可选取消参数，并传递到预览、纸张预览和导出调用。
- OCR 分类和识别每批最多 4 行；识别的填充宽度乘批量大小不超过 2048，单段归一化宽度最多 2048。当前固定模型在此预算下的识别概率输出约不超过 36 MiB。直接在 ONNX 输出持有期完成解码，不再整体复制识别输出数组。
- 普通文字行仍只方向分类一次。宽高比超过 32 的长行使用首、中、末局部窗口投票，避免方向缩略图把整行挤成无法判断的细线。
- 超长行寻找安静列切分，保留约 192 个归一化像素的重叠；空间约束的最长公共子序列对齐重叠字符，避免把相邻的重复号码合并。最终恢复原阅读顺序。
- 字符位置通过逆透视变换映射，包含方向旋转和分段偏移。字符边界仍属于 CTC 时间位置估计，不能视为逐字人工真值定位。
- 请求内的 `PreparedPage` 共享一次原图解码和几何校正，分别生成 OCR 与扫描分支；不缓存图像上下文。原文件版本在处理前后检查，版本不匹配不发布结果。OCR 缓存加入扫描算法版本，失效规则保持有效。
- 原有 OCR 结果类型及导出选项保持不变；`ConvertColor` 新增可选 `CancellationToken`，现有源码调用可直接重建。修改后已重建整个解决方案。
- 极端长宽比的分析图最小边保证至少一个像素，避免缩放到零。

## 对照实验

基线为本次修改前保存的 Core DLL，SHA-256：`654673C19ED55DA2B7F8094E286E088BC85DB9C364AC241EB2545D67804DDC23`。基线和新版在独立进程的独立程序集加载上下文中执行，报告记录实际加载路径、程序集哈希及方法参数数目，防止版本混用。

环境：AMD Ryzen 7 8845H，8 核 16 线程，系统可见内存约 27.8 GiB；OpenCV 固定 4 线程，ONNX 保持最多 4 个线程。速度测试先预热，每尺寸 15 次，P95 使用最近秩统计。冷启动只有单次观测，不作为稳定分位数。内存是进程峰值工作集，较大尺寸包含先前尺寸的运行历史，不是只计算数组大小。

### 速度与内存

| 场景 | 旧版中位数 ms | 新版中位数 ms | 新/旧 P95 | 旧峰值 MiB | 新峰值 MiB |
|---|---:|---:|---:|---:|---:|
| 黑白 8 MP | 144.3 | 125.3 | 0.795 | 265.4 | 128.4 |
| 黑白 12 MP | 213.9 | 190.5 | 0.893 | 381.3 | 162.9 |
| 黑白 24 MP | 420.8 | 399.3 | 0.960 | 725.8 | 263.6 |
| 增强 8 MP | 161.5 | 154.1 | 0.974 | 271.0 | 232.1 |
| 增强 12 MP | 230.7 | 218.3 | 0.928 | 387.0 | 326.3 |
| 增强 24 MP | 445.7 | 407.3 | 0.813 | 731.4 | 601.5 |

黑白结果在以上三个尺寸上的 PNG 哈希与旧版一致；另有随机灰度、极小图片和跨条带细线的逐像素一致性测试。黑白峰值内存降低约 52%–64%，中位耗时降低约 5%–13%；增强中位耗时降低约 5%–9%。这些观测仅适用于当前机器和测试输入。

12 MP 的“增强后黑白转换”连续循环中，10 页中位耗时 437.3 → 410.5 ms，30 页 449.3 → 411.1 ms。30 页进程峰值工作集 420.6 → 323.3 MiB；新版最后 5 页工作集约 79.2–80.0 MiB，趋于稳定。这是计算管线循环，不含磁盘导入、OCR 或 PDF 编码耗时，也不是 30 份不同证件的完整导出计时。

### 质量与识别

| 测试 | 旧版 | 新版 | 解释 |
|---|---:|---:|---|
| 独立 12 张合成彩色阴影图，全图 RGB MAE | 13.303 | 5.627 | 4 种彩色底纹 × 3 种阴影，固定真值，12 张均改善。 |
| 同一组彩色区域 RGB MAE | 19.181 | 3.524 | 专门检查底色保留，不用锐化分数替代。 |
| H-DIBCO 2018，10 张平均前景 F1，完整增强后黑白 | 0.6272 | 0.6362 | 真实退化手写图像，仅补充笔画保留验证，不表示手写 OCR 达到验收。 |
| 同一 DIBCO 集直接二值化 F1 | 0.62687 | 0.62687 | 统计与条带重构没有改变直接二值化质量。 |
| 既有中英文/数字清晰与阴影样本 CER | 0 | 0 | 保持回归；简单样本零错误不能证明一般识别率提高。 |
| 336 字符超长行 CER，给定相同文字框 | 96.73% | 0.30% | 主要修复极端长行的方向缩略图及压缩损失；不是整页检测与识别的总体准确率。 |

超长行识别约 276 → 542 ms，以更多原始细节采样换取可读结果；该极端质量用例单独报告，不宣称长行加速。普通五行中英文样本热运行中位数约 225.6 → 240.8 ms（+6.8%，预算内），冷启动约 710 → 758 ms，模型保持不变。

合成开发样本与 12 张独立验收样本分开；验收图参数固定后未据其结果继续调参。合成样本使用 `ShadowRemoval=1`，关闭自动曝光、高光、颜色增强、降噪和锐化，以单独比较照明估计。既有完整回归仍覆盖日常默认设置、印章色相、小字与浅线。

H-DIBCO 使用标准前景 F1，自行计算；没有把它称为官方伪 F 值、DRD 或官方竞赛总分。各图结果在原始 JSON 中，不能用均值掩盖单图退化。

## PDF 与稳定性

另对 6400×2100 纯白图进行了数值压力检查：201×201 窗口的平方和为 2,627,075,025（超过 Int32 上限），输出仍为全白，未发生累加溢出。

完整 246 项断言覆盖原有裁剪（背景混入 <2.5%）、细线 F1=1、印章色相、缩放缓存、批量取消、源文件变化、坏模型、失败不静默导出，以及新增的长行、混合宽度批处理、逆透视字符坐标和极端长宽比。

`VerifyOcrPdf.py` 独立检查 3 个 PDF：中文 Unicode 提取、不可见文字模式及全部字符排版矩阵通过；每页一张、连续排版和旋转弯边组合由回归覆盖。文字层启用前后，渲染像素一致。

## 数据来源与限制

- [H-DIBCO 2018 官方数据与真值](https://vc.ee.duth.gr/h-dibco2018/benchmark/)：下载约 25 MB，SHA-256 固定在下载脚本和 manifest。官方提供科研基准，但未发现明确再分发许可；仅存放在被忽略的 artifacts，不随程序发布。
- [GCDRNet / RealDAE 作者仓库](https://github.com/ZZZHANG-jx/GCDRNet)：固定提交 `415c97a9a64d0a796dbbad43485ce2ab102f2a7d` 的一张中文书页照片用于前后目视检查，仓库代码标示 MIT，图片未发现单独授权，本次仅作本地验证。本次没有该照片的人工配对真值或逐字转录，不能给它计算真实 CER/PSNR，也不将模型生成的 enhanced 示例当成真值。
- 未下载完整 RealDAE、9 GB SD7K 或其训练权重。没有原始真实证书/证件参考照片，所以真实证件和更广泛中英混排仍需补充验收，不能从合成集外推“所有照片识别率提高”。
- 本次未加入大型图像恢复网络；新增模型体积 **0 字节**，固定模型及字典资产仍约 22.5 MB。Core DLL 从 150,528 增至 167,424 字节（增加 16.5 KiB）；未重新打包便携目录，不报告未经测量的新便携包体积。

## 重现入口

```powershell
# 全部回归与新增算法专项
dotnet run --project tests/EasyScan.Tests -c Release -- artifacts/research-final
dotnet run --project tests/EasyScan.Tests -c Release -- artifacts/research-focused --research-only
python scripts/VerifyOcrPdf.py artifacts/research-final/扫描优化/pdf-checks.json

# 验证集下载只用于开发；运行程序不需要 Python 或网络
python scripts/FetchResearchSamples.py

dotnet build scripts/EasyScan.Benchmarks -c Release
# 两个版本必须分进程运行；baseline DLL 是修改前保存在 artifacts/research-baseline 的快照
# 完整的前后对照与所有验收门槛（按顺序运行，避免相互干扰）
powershell -ExecutionPolicy Bypass -File scripts/RunResearchBenchmarks.ps1

# 也可单独测量 binary；第三个参数还支持 enhance / quality / holdout / batch / dibco / ocr / ocr-long / real / numeric
dotnet scripts/EasyScan.Benchmarks/bin/Release/net10.0-windows/EasyScan.Benchmarks.dll artifacts/research-baseline/EasyScan.Core.dll artifacts/research-bench/baseline-binary.json binary 15
dotnet scripts/EasyScan.Benchmarks/bin/Release/net10.0-windows/EasyScan.Benchmarks.dll src/EasyScan.Core/bin/Release/net10.0-windows/EasyScan.Core.dll artifacts/research-bench/current-binary.json binary 15
python scripts/VerifyResearchBenchmarks.py
```

基准输入固定，工具不改动用户文件。独立验证器检查速度预算（中位数 ≤110%、P95 ≤120%）、黑白内存至少降低 20%、像素一致性、质量均值、普通 OCR 无退化、长行改善及批量工作集稳定。它要求对应模式的前后 JSON 都已生成。

运行产物：`artifacts/research-final/` 为完整回归，`artifacts/research-bench/` 为逐项指标和前后图，`artifacts/research-data/manifest.json` 为来源和校验记录。基准样本、下载文件、旧 DLL 及输出图片均不进入便携包。


## 对比图

合成开发图（输入含阴影；真值、旧版及新版并列见本地 `artifacts/research-bench/comparison.html`）：

![新版彩色阴影校正](artifacts/research-bench/current-quality-output.png)

真实中文书页的原图、旧版和新版也在对比页中；仅作目视检查，不能据此声明真实证件精度。
