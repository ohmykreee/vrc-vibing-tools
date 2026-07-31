# Vibing Tool-sets for VRChat Project
一些为了 VRChat 改模借助 AI 写的一些小工具。使用 MIT 协议开源。
1. 不保证代码质量/功能健全；
2. 不对把你项目弄崩负责；
3. 没有义务提供使用指导。

## 安装/使用方法
1. 克隆/下载整个项目；
2. 将 `src` 文件夹下的工具代码复制到 `Assets/Editor` 下；
3. 如果要更新代码，删掉老代码然后复制进新代码。

或者：

1. 去 [Releases](https://github.com/ohmykreee/vrc-vibing-tools/releases) 中下载最新的 UnityPackage 然后双击导入；
2. 因为已经将文件 GUID 打包进包中所以更新直接重新导入新包。

## 工具简介
1. BlendShapeSync：最早的小工具，同步两个相同模型的 BlendShape 状态，基本上用不着。
2. Vpd2Anim：将 MMD 的 VPD 文件转换为单帧 Humanoid 可用的 Anim 文件。
3. PoseBankBuilder：将多个姿势 Anim 文件合并为 [BUDDYWORKS Poses Extension](https://repo.buddyworks.wtf/) 可用的 PoseBank 格式。