# LivePhoto 修复器

Windows 桌面工具：把被 iPhone 互传破坏的实况照片（Live Photo）批量修复为**绿联 NAS 可正确识别**的 Google Motion Photo 1.0 格式。

**非侵入**：原文件全程只读，输出目录禁止落在源目录内，校验不过不落盘。

- 平台：Windows 10/11（x64），.NET 8 + WPF，零第三方依赖
- 许可：[MIT](LICENSE)

## 功能

- 批量扫描照片库，自动识别三类缺陷：Apple MakerNote 改写、缺 Google 实况声明、视频切片缺 `ftyp` 容器头
- 三级漏斗扫描（枚举 → 头部探测 → 全量分析），针对远程盘 / NAS 优化，千张目录秒级完成头部探测
- 修复只动元数据：写标准 Google XMP、处理 MakerNote；封面像素、内嵌视频、GPS 一个字节不动
- 不伪造设备身份：仅 `Make=vivo` 写 vivo 原生值，否则删除标签
- 写入前逐项校验 + 原子落盘，失败条目源文件零改动
- 按设备/机型筛选、导出修复报告（含每条 SHA256）

## 快速开始

### 直接用

取仓库内 `publish/portable/LivePhoto.App.exe`（约 300 KB），
需目标机已装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)。
发布页地址待定。

### 从源码构建

前置：.NET 8 SDK、Windows。仓库不含 SDK。

```bash
git clone <仓库地址待定> live-repair
cd live-repair

dotnet build LivePhoto.sln -c Release
dotnet run --project src/LivePhoto.App -c Release
```

打发布包：

```bash
# 便携版（约 300 KB，需 .NET 8 桌面运行时）
dotnet publish src/LivePhoto.App -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish/portable

# 开箱版（约 162 MB，零依赖）
dotnet publish src/LivePhoto.App -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish/standalone
```

## 使用

打开文件夹有三种方式：界面按钮、把文件夹拖进窗口、命令行传路径
（`LivePhoto.App.exe "<照片目录>"`）。

流程：打开目录 → 等枚举出设备列表 →「分析所选」→「开始修复」→「导出报告」。
扫描并发度固定为 1（远程盘/NAS 实测最优，非 bug）。

## 配置（主界面默认值）

| 项 | 默认 | 说明 |
|---|---|---|
| 启用前缀过滤 | 开 | 总开关；不勾 = 不限文件名 |
| 前缀 | `LivePhoto` | 逗号分隔；枚举阶段生效，改完需重新扫描 |
| MakerNote 策略 | 自动 | 或选「总是删除」 |
| 修复模式 | 保留模式 | 或选「清理模式」（删 MP4 尾部杂散字节） |
| 输出目录 | 空 | 必须在源目录之外，三层校验不可绕过 |

## 常见问题

- **杀软误报**：单文件 exe 运行时会自解压到临时目录，易被启发式引擎误判。优先代码签名，否则加 Defender 排除项，或改用非单文件发布。
- **“输出目录不可用”**：输出目录不能在源目录内，换一个即可。
- **扫描慢**：并发度固定 1 是 NAS 场景的实测最优值。
- **窗口起不来**：看 `%TEMP%\livephoto_ui_error.txt`（内置全局异常捕获）。
- **首次启动 1~3 秒**：单文件 exe 自解压缓存，正常现象。
- **中文路径**：程序内部走 Unicode API 没问题；别用 bat/ps1 脚本传中文参数。
- **仓库里没有 tests/**：测试套件与真实样本未随仓库分发（见 `.gitignore`）；本地开发可跑 `dotnet test tests/LivePhoto.Core.Tests/LivePhoto.Core.Tests.csproj -c Release`。

## 已知限制

- 仅 Windows（WPF，无跨平台版）
- iPhone 原生 Live Photo（HEIC/JPG + 外挂 MOV）无法转换，需视频重编码，未实现
- HEIC / HEIF 不支持，直接跳过
- 只修元数据与容器头，不重编码视频
- 修复后 `Make=Apple` 保留原值（不伪造设备身份），绿联是否因此降级需真机验证
- 开箱版 / 安装包（Inno Setup 脚本在 `installer/`）未随仓库分发，需本地构建
