# 历史听歌识曲方案归档 (Archived Recognition Methods)

本目录归档了项目在研发探索阶段使用过的三种历史识曲实现，仅作为技术演进参考：

1. **`native_runner/`**:
   - 基于微型 Bionic 运行环境、C 语言管道胶水代码与 QEMU ARM64 模拟器的外部 Runner 方案。
   - 现已被纯 C# 托管代码原生算法 [`AcousticFingerprintExtractor.cs`](../src/Services/AudioRecognition/AcousticFingerprintExtractor.cs) 完全替代。

2. **`shazam/`**:
   - 基于频域星座图峰值提取、48 字节二进制签名编码与苹果云端接口交互的 Shazam 实现。

3. **`acrcloud/`**:
   - 基于 HMAC-SHA1 签名与外部第三方开放平台 API 的 ACRCloud 识别方案。
