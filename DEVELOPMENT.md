# 实现与验证记录

目标版本：Emby 4.10.1.0，.NET 8，InfuseSync 1.5.3.0。独立项目插件 ID 为 `b56f2e37-909b-4439-8b31-bb51ca4acdc1`。

旧项目的 `PluginOptions.ShowConflictPluginLoadedStatus` 按已加载程序集名检查 InfuseSync；这能产生明确警告，但本身不能证明用户出现的所有运行故障。旧项目还包含 Harmony、媒体源修改和多种事件处理。新项目通过限制职责并去掉这些运行依赖避免共享补丁面，不修改原项目。

算法使用 FFmpeg Chromaprint algorithm 1 的 raw 32 位指纹。时间步长参考 Chromaprint 官方配置的 1365/11025 秒，窗口延迟由其滤波器配置计算。候选偏移来自多段指纹投票，随后按汉明距离、连续覆盖率和指纹多样性验证；候选需要独立集数支持。片头边界向内收缩，避免将匹配窗口延迟计入跳过目标。尾部匹配仅生成 CreditsStart。

参考实现说明：
- https://github.com/acoustid/chromaprint/blob/master/src/fingerprinter_configuration.cpp
- https://github.com/acoustid/chromaprint/blob/master/src/fingerprinter_configuration.h
- https://github.com/firecore/InfuseSync

互操作验证使用官方 InfuseSync v1.5.3 安装包，其源提交为 88a9f32ae7bd76b6877583181821d59745864097。该第三方 DLL 只在本地隔离测试中使用，不包含在安装包或源码包中。

服务端单元及真实音频检查和真实隔离宿主检查的最终结果见 VERIFICATION.json。测试次数不是对所有媒体的识别率承诺。未完成飞牛 Linux 实机探测、用户真实剧集抽样和 Apple TV 跳过按钮验收。

任务只在被明确选中的媒体库父子链范围内操作。首次隔离测试发现手工创建的媒体项没有完整祖先索引，因此使用明示的父链核验范围而非将空 SQL 结果当作成功。测试 harness 显式建立实际父项关系，验证真实数据库写入和 InfuseSync 更新记录。

并发约束：插件自身的探测和清理共享单个信号量；FFmpeg 每次一个进程；不拦截 InfuseSync 的锁或事件。章节写入前重新读取并合并，保留已有标记。不能与另一个同时强制重写章节的探测器互相提供全局事务，因此不建议并行运行旧 Film Index 的标记任务。
