# Skip-emby

面向 Emby 4.10.1.0 的独立片头片尾音频探测插件。插件显示名称与安装文件均为 `Skip-emby`，拥有自己的插件 ID、配置、任务和数据目录。

本项目只做同季剧集的片头、片尾探测与标准章节标记管理。不会加载 Film Index、StrmAssistant、Harmony 或 InfuseSync 作为运行依赖，不注入网页脚本、不接管播放、不修改媒体源、不调整授权状态，也不修改原 Film-Index-Emby 仓库。旧项目的冲突警告不会因本插件安装而消失。

## 工作方式

- 调用 Emby 自带 FFmpeg 的 Chromaprint 算法，串行读取同季多集的首尾音频片段。
- 用多个独立剧集交叉匹配重复音频，并保守收缩片头边界；低信息音频、短匹配及不足参考集数不写标记。
- 通过 Emby 标准 `IntroStart`、`IntroEnd` 和 `CreditsStart` 章节类型保存结果，调用标准媒体更新事件让 InfuseSync 获知变化。
- 已有同类标记优先保留，清理任务只移除 `Skip-emby:` 命名的本插件标记。修改章节前保存备份。
- 默认不启用、不选择库、只试运行、禁止远程片段读取，没有自动运行计划。用户明确选择后再执行。

这不是语义理解型片尾识别。没有重复片尾音乐、仅一集、没有音轨、特殊重剪辑以及变速音频可能无法匹配。FFmpeg 必须支持 `chromaprint`；播放器是否显示跳过按钮取决于客户端自身。初版只处理剧集，不支持电影自动片尾。

## 与 InfuseSync 共存

参考官方 [firecore/InfuseSync](https://github.com/firecore/InfuseSync)，针对用户已安装的 **InfuseSync 1.5.3.0** 核验。新插件没有旧项目的 InfuseSync 黑名单检查和全局运行时补丁。

测试包括真实 Windows Emby 4.10.1.0 隔离进程：两插件同时初始化、运行实际生成音频的探测、写入标准章节、验证 InfuseSync 收到两集更新且同步检查点仍存在。没有在用户飞牛容器上替换现有 DLL，没有完成真实 iPhone/Apple TV 的新插件验收，不能据此保证所有版本和客户端。

InfuseSync 自己的内部行为没有被修改。建议切换时将旧 FilmIndex.Emby.dll 移到插件目录外保存，并保留旧配置；否则旧插件仍可能发出自身的冲突警告或运行重复任务。

## 安装

停止 Emby 容器并备份其配置目录，将 `Skip-emby.dll` 放到原 Emby 持久化目录的 `plugins` 子目录，重启原容器。无需重建媒体库或更换镜像。详细步骤见安装包内 `安装教程.txt`。

打开 Emby 插件页的 Skip-emby：启用、选择电视剧库、保留试运行，STRM 用户另勾选允许远程读取。在计划任务中手动运行“Skip-emby 片头片尾探测”。确认结果后关闭试运行，再执行一次写入。可在计划任务中自行设置计划，默认不会自动跑全库。

音频探测必须从 NAS 读取首尾片段，会产生分析流量和 FFmpeg 开销，但不修改 302 播放链路。默认单进程抽取、每季最多 12 集、每次最多 20 季。每次优先选择缺失标记的集数并补充参考集，保留已有标记的剧集不会被覆盖。

## 构建与测试

需要 .NET 8 SDK 和官方 Emby 4.10.1.0 `system` 目录，仅作引用，不随安装包分发。

```powershell
dotnet build src/SkipEmby/SkipEmby.csproj -c Release -p:EmbyServerPath=C:/path/to/emby/system
dotnet run --project tests/SkipEmby.Tests/SkipEmby.Tests.csproj -c Release -p:EmbyServerPath=C:/path/to/emby/system -- C:/path/to/ffmpeg.exe C:/isolated/fixtures C:/official/InfuseSync.dll
```

`SkipEmby.Tests` 包含真实 FFmpeg 音频生成和 AAC/WAV 匹配。`SkipEmby.HostTests` 是独立 Emby 进程使用的测试插件，只允许带 `.fixture-only` 标记的 `skip-emby-host-*` 目录，不能装到正式服务器，也不会出现在安装包内。

核心探测实现不使用服务器私有指纹管理器，不修改服务器超时字段、其他插件数据库或补丁链。源码及许可证另附，遵循 GPL-3.0。Chromaprint 由所调用的 FFmpeg 提供；本包不重新分发 FFmpeg、Chromaprint、InfuseSync 或 Emby 的 DLL。
