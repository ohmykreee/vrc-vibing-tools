# AGENTS.md

## 项目结构

- `src/<工具名>/`：各 Unity 编辑器工具的 C# 源码（.cs + 对应 .meta），发布时拷入用户工程的 `Assets/Editor`。
- `unity/`：配套 Unity 工程。目标 Unity 版本以 `unity/ProjectSettings/ProjectVersion.txt` 的 `m_EditorVersion` 为准。
- `ref-dlls/`：本地编译检查用的 Unity 托管程序集（已在 .gitignore 中，不入库）。

## 无 GUI 环境下的编译检查

本机是无 GUI 的远程开发环境，**没有也无法运行 Unity Editor**。改用「Roslyn csc + Unity 托管 DLL + .NET 8 引用程序集」对 `src/` 下的编辑器脚本做编译期验证。

### 依赖清单与位置

| 依赖 | 版本 | 位置 |
|---|---|---|
| .NET SDK（含 csc） | 8.0.423 | 需要自行查找，如果本机安装 version-fox 则在 `~/.version-fox/sdks/dotnet/dotnet`（实体在 `~/.version-fox/cache/dotnet/`） |
| libicu | 76 | 系统库 `/usr/lib/x86_64-linux-gnu/` |
| net8.0 引用程序集 | 随 SDK 自带 | 同 .NET 要求，`~/.version-fox/cache/dotnet/**/packs/Microsoft.NETCore.App.Ref/*/ref/net8.0/` |
| Unity 托管 DLL | 2022.3.22f1 | `ref-dlls/2022.3.22f1/Managed/UnityEngine/*.dll`（含 UnityEngine/UnityEditor 全部模块的扁平完整集） |

要点：

- Unity 的 DLL 引用 `netstandard 2.1.0.0`，由 net8.0 ref pack 里的 `netstandard.dll` 精确满足；因此 `ref-dlls/` 下的 `MonoBleedingEdge/` 文件夹**不参与**编译，无需使用。
- `ref-dlls/` 下的版本目录名必须与 `unity/ProjectSettings/ProjectVersion.txt` 的版本一致；Unity 版本升级时需同步重新拷贝。

### 依赖缺失时如何提示用户

逐项按下表检查，缺失时**停止并提示用户补充**，不要尝试自行安装系统包或从网络下载 Unity：

1. **dotnet 不存在**（`~/.version-fox/sdks/dotnet/dotnet --version` 失败）
   → 提示：「请安装 .NET SDK 8（本机使用 version-fox 管理，可 `vfox add dotnet`，或参考 https://dotnet.microsoft.com/download）。」
2. **dotnet 运行报 `Couldn't find a valid ICU package`**
   → 提示：「请安装 libicu：`sudo apt install -y libicu-dev`（Debian/Ubuntu）。」
3. **找不到 csc.dll**（`find ~/.version-fox/cache/dotnet -name csc.dll -path '*Roslyn*'` 为空）
   → 提示：「.NET SDK 安装不完整，请确认 `dotnet --list-sdks` 输出包含 8.x。」
4. **找不到 net8.0 ref pack**（`find ~/.version-fox/cache/dotnet -path '*Microsoft.NETCore.App.Ref*/ref/net8.0/netstandard.dll'` 为空）
   → 提示：「.NET SDK 的 ref pack 缺失（正常随 SDK 自带于 packs/ 目录），请重新安装 .NET SDK 8。」
5. **`ref-dlls/<版本>/Managed/UnityEngine/` 不存在或没有 DLL**
   → 提示：「请从装有 Unity <版本>（见 unity/ProjectSettings/ProjectVersion.txt）的机器（如 Windows）拷贝 `<Unity安装目录>\Editor\Data\Managed\` 整个文件夹，放到仓库根的 `ref-dlls/<版本>/Managed/`（保证存在 `ref-dlls/<版本>/Managed/UnityEngine/UnityEngine.CoreModule.dll` 等文件），并确认 `.gitignore` 已忽略 `ref-dlls/`。」

### 使用方法（依赖齐全时）

在**仓库根目录**执行（`TOOL` 换成目标工具目录名，这里以 `Vpd2Anim` 举例）：

```bash
TOOL=Vpd2Anim
UNITY_VER=$(grep -m1 'm_EditorVersion:' unity/ProjectSettings/ProjectVersion.txt | awk '{print $2}')
UNITY_DLLS="ref-dlls/$UNITY_VER/Managed/UnityEngine"
DOTNET=~/.version-fox/sdks/dotnet/dotnet
CSC=$(find ~/.version-fox/cache/dotnet -name csc.dll -path '*Roslyn*' | sort -V | tail -1)
NETREF=$(dirname "$(find ~/.version-fox/cache/dotnet -path '*Microsoft.NETCore.App.Ref*/ref/net8.0/netstandard.dll' | sort -V | tail -1)")

{
  echo "/nologo"; echo "/t:library"; echo "/nostdlib"; echo "/langversion:9.0"
  echo "/out:/tmp/${TOOL}-check.dll"
  find "$NETREF" -maxdepth 1 -name '*.dll' | sed 's/^/-r:/'
  find "$UNITY_DLLS" -maxdepth 1 -name '*.dll' | sed 's/^/-r:/'
  ls "src/$TOOL"/*.cs
} > /tmp/${TOOL}-check.rsp

"$DOTNET" "$CSC" @/tmp/${TOOL}-check.rsp && echo "编译通过"
```

无输出且打印「编译通过」即为成功；任何 `error CSxxxx` 都需先修复。检查完后删除 `/tmp/${TOOL}-check.rsp` 与 `/tmp/${TOOL}-check.dll`。

### 能力边界

- **能**：检出语法错误（CS1xxx）、类型/符号错误（CS0117、CS0246 等），与 Unity 编译编辑器脚本的结果等价。
- **不能**：运行代码、执行 EditMode 测试、验证 Unity 运行时行为（IMGUI 事件、拖放、SceneView 等）。涉及运行时行为的改动，须提示用户在 Unity Editor 内手动验证。
