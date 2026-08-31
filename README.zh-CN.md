# ClipToGif

[English](README.md)

Windows 小工具：从视频里选出一段，导出成一条或多条 GIF。

导入视频、在时间轴上框选区间、设置尺寸 / 帧率 / 质量 / 压缩算法后导出。一个视频可以对应多条 GIF。

![主界面](docs/images/main.png)

## 功能

- 导入或拖入视频（只链接路径，不会复制或移动原文件）
- 预览播放，优先硬件加速，不支持则自动降级
- 拖动绿色区间选取片段（长度不限）
- 可调宽度、高度、帧率、质量（1 更清晰 → 10 更小体积），可选保持宽高比
- 可选 GIF 压缩算法：默认不压缩，另有多种无损 / 有损算法
- 右侧 GIF 列表：缩略图、打开文件 / 目录、删除
- 中文 / English 界面切换
- 源视频缺失时仍保留条目并给出提示

![已生成 GIF](docs/images/result.png)

## 下载

到 [Releases](../../releases) 下载最新的 `ClipToGif-1.2.0-win-x64.zip`。

发版在 GitHub Actions 的 **Release** 工作流里手动触发。发版前改 `ClipToGif.csproj` 里的 `<Version>`。

解压后直接运行 `ClipToGif.exe`。不用装 Visual Studio、.NET SDK，也不用另装 FFmpeg——运行时和 FFmpeg 都已打进包里。

**系统要求：** Windows 10/11，64 位。

## 用法

1. 左侧导入或拖入视频。
2. 播放预览，拖动绿色选区确定片段。
3. 调整 GIF 宽高、帧率、质量和压缩算法。
4. 点击 **生成 GIF**，结果出现在右侧列表。

库数据和导出的 GIF 保存在 `%LocalAppData%\ClipToGif\`。

## 源码编译

```powershell
dotnet publish ClipToGif.csproj -c Release -r win-x64 --self-contained true -o publish
```

FFmpeg 7.x 共享库需放在 `ffmpeg\`（含 `avcodec-61.dll`、`ffmpeg.exe` 等）。Release 工作流打包时会自动下载。

## 许可说明

本应用内置了 [FFmpeg](https://ffmpeg.org/) 共享库（GPL），具体条款以 FFmpeg 项目为准。
