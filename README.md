# SolidWorks 接入（DSH 插件）

> **English:** A DeepSeek Harness plugin that drives the real SOLIDWORKS desktop app through its COM API — parametric part modelling (sketch, extrude, cut, revolve, loft, sweep, shell, surface, fillet, chamfer), assembly component insertion with plane-to-plane mates, drawing creation, inspection, metadata, export and screenshots. Windows + a licensed SOLIDWORKS installation are required. This is an unofficial community project and is not affiliated with or endorsed by Dassault Systèmes.

让 DeepSeek Harness 直接操作本机真实的 **SOLIDWORKS**：零件建模、装配体插入与配合、工程图生成，以及打开、检查、导出、自定义属性和截图。

## 安装

从 npm 安装（推荐）：

```
@pan-318/dsh-solidworks
```

在 DeepSeek Harness 里让 Agent 执行 `plugin_manager` 的 `install_bundle`，`target` 填 `@pan-318/dsh-solidworks`。

也可以从源码安装：

1. 克隆仓库，得到包含 `package.json` 的插件目录。
2. `install_bundle` 的 `target` 指向该目录的**绝对路径**。
3. 重启或刷新会话后即可使用 `solidworks_*` 工具。


## 环境要求

- Windows 10/11 x64
- SOLIDWORKS 2014 或更新版本（已注册 `SldWorks.Application` COM 接口）
- .NET Framework 4.x（Windows 自带，插件用它把 C# 驱动编译到 `%LOCALAPPDATA%\dsh-solidworks`）

插件不修改 SolidWorks 安装；建模只在打开着的文档里进行，不会自动保存。

## 工具

| 工具 | 作用 |
| --- | --- |
| `solidworks_status` | 是否安装、是否运行、版本、当前打开的文档（不改动任何东西） |
| `solidworks_open` | 打开 `.sldprt/.sldasm/.slddrw`，或用默认模板新建零件/装配体/工程图 |
| `solidworks_model` | **零件建模**：草图（矩形/圆/多边形/折线/中心线）、拉伸、切除、旋转、放样、扫描、抽壳、曲面、圆角、倒角 |
| `solidworks_assembly` | **装配**：插入零部件、平移定位、平面配合（重合/距离/平行/垂直/同轴/相切/角度）、固定/浮动 |
| `solidworks_drawing` | **工程图**：从零件/装配体生成标准视图（前/上/右/左/后/下/等轴测）、插入模型尺寸、列出视图 |
| `solidworks_documents` | 列出已打开的文档、活动文档、未保存状态 |
| `solidworks_inspect` | 配置、材质、质量属性、包围盒、实体/曲面实体数、特征树、零部件、图纸、自定义属性；`edges=true` 列出编号边（直线给出端点，圆形边给出圆心、半径、周长） |
| `solidworks_properties` | 读取 / 写入 / 删除自定义属性（文档级或指定配置） |
| `solidworks_export` | 导出副本：pdf / step / iges / stl / x_t / x_b / 3mf / png / jpg / tif；dxf、dwg 需从工程图导出 |
| `solidworks_capture` | 截图当前图形区并作为图片返回给模型（可核对模型外观） |
| `solidworks_save` | 保存、另存为、关闭（默认拒绝丢弃未保存修改） |

## 零件建模（`solidworks_model`）

尺寸一律**毫米**；步骤按顺序执行，`extrude` / `cut` / `revolve` 默认使用上一步 `sketch` 的草图。

```json
{
  "steps": [
    { "op": "sketch", "plane": "front", "shapes": [
      { "type": "rectangle", "width": 100, "height": 60 }
    ]},
    { "op": "extrude", "depth": 20 },
    { "op": "sketch", "plane": "front", "shapes": [
      { "type": "circle", "center": [0, 0], "radius": 6 }
    ]},
    { "op": "cut", "depth": 10, "offset": 20 },
    { "op": "fillet", "radius": 3 }
  ]
}
```

- `sketch.plane`：`front` / `top` / `right`、已有基准面名称，或从 1 开始的序号；`sketch.offset` 会创建一个偏移基准面（预览时自动隐藏）。
- 草图图形：`rectangle`（`center`、`width`、`height`）、`circle`（`center`、`radius`）、`polygon`（`sides`、`rotation`）、`polyline`（`points`、`closed`）、`centerline`（两点，旋转特征的轴）。
- `extrude`：`depth`、`direction`（`blind` / `midplane`）、`offset`（起止距离）、`flip`。
- `cut`：默认 `throughAll`；给 `depth` 则为盲切，可用 `offset`、`flip`、`bothDirections`。
- `revolve`：`angle`（默认 360），草图中需要 `centerline` 和闭合轮廓。
- `loft`：`profiles`（两个以上草图名）；省略时用最后两个草图。
- `sweep`：`profile` / `path`；省略时最后一个草图为轮廓、前一个为路径。
- `shell`：`thickness`、`faces`（`top/bottom/front/back/left/right/none`）、`outward`。
- `surface`：`depth`，把草图拉伸成曲面。
- `fillet` / `chamfer`：作用于全部边，或用 `edgeIndices`（先 `solidworks_inspect` 加 `edges=true` 取得编号）只处理指定边。

完成后自动旋转到等轴测、缩放到合适大小，并附一张隐藏基准面/草图的预览截图。

## 装配（`solidworks_assembly`）

```json
{
  "steps": [
    { "op": "insert", "path": "D:\\parts\\底座.SLDPRT", "fix": true },
    { "op": "insert", "path": "D:\\parts\\盖板.SLDPRT", "x": 0, "y": 0, "z": 80 },
    { "op": "mate", "type": "coincident", "componentA": "底座-1", "planeA": "top", "componentB": "盖板-1", "planeB": "bottom" },
    { "op": "mate", "type": "distance", "componentA": "底座-1", "planeA": "right", "componentB": "盖板-1", "planeB": "right", "distance": 30 }
  ]
}
```

- `insert` 会自动先在后台打开目标文件（SolidWorks 只能插入已加载的文件），支持 `configuration` 与 `fix`。
- 配合按**零件基准面**建立：`planeA/planeB` 用 `front/top/right` 或基准面名；距离用 `distance`（mm），角度用 `angle`（度），`flip` 反向。
- 已保存的装配体需要 `allowEdit: true`，改动前会备份到 `%LOCALAPPDATA%\dsh-solidworks\backups`。

## 工程图（`solidworks_drawing`）

```json
{ "model": "D:\\parts\\底座.SLDPRT", "views": ["front", "top", "right", "iso"], "autoDimensions": true }
```

- 用 SolidWorks 默认工程图模板新建图纸，按图纸大小自动排布视图。
- `action: "views"` 列出当前工程图的视图。
- 生成后用 `solidworks_save` 保存为 `.SLDDRW`，再用 `solidworks_export` 导出 PDF。

## 保存与导出规则

- **新建文档没有文件名**：`action=save` 会失败并提示改用 `action=save_as`，必须给 `out`。
- `save_as` 只写 `.SLDPRT` / `.SLDASM` / `.SLDDRW`，且扩展名必须与文档类型一致；漏写扩展名会自动补上，写错（比如给零件存 `.SLDASM`，或给 `save_as` 传 `.step`）会直接报错。
- **导出其它格式请用 `solidworks_export`**：它总是写副本（`swSaveAsOptions_Copy`），不会把打开的文档改成导出文件；`format` 与 `out` 扩展名不一致会被拒绝，避免 SolidWorks 把零件“另存成” `.SLDPRT` 而改掉文档路径。
- 相对路径按**文档所在目录**解析；文档还没保存时按“文档”文件夹解析，不会落到插件源码目录。

## 无界面运行

默认 `visible: false`，SolidWorks 在后台运行，不会弹出窗口：

- 建模、检查、装配、工程图、导出、保存都不需要窗口，全部可用。
- **截图和 PNG 导出也照常工作** —— SolidWorks 仍然在内存里渲染图形区，只是窗口不显示。
- 打开文档时 SolidWorks 会自己把窗口弹出来，插件在每次操作结束时会重新隐藏，所以你不会看到它一闪而过。
- 冷启动时进程以隐藏方式启动，连启动画面都不出现。
- 想看着它操作，把配置改成 `visible: true` 即可，两种模式随时切换。

## 安全设计

- 零件、装配体、工程图都可以操作；**只读文档拒绝改动**。
- 已经保存到磁盘的零件/装配体/工程图，**改几何、改属性、删属性**都必须传 `allowEdit: true`，否则拒绝；允许时先把原文件复制到 `%LOCALAPPDATA%\dsh-solidworks\backups` 并在结果里返回备份路径。
- 所有改动**不自动保存**，需要显式调用 `solidworks_save`；关闭文档时默认拒绝丢弃未保存修改。
- 每一步失败会指出第几步、哪个特征失败；预览截图默认隐藏基准面和草图，看不清模型时可以再用 `solidworks_capture`。

## 配置（`cordis.patch.yml`）

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `visible` | `false` | 是否显示 SolidWorks 窗口；默认**无界面后台运行** |
| `autoLaunch` | `true` | 需要时自动启动 SolidWorks（首次约 30 秒） |
| `timeoutSec` | `180` | 单次自动化操作最长等待时间 |
| `captureWidth` / `captureHeight` | `1400` / `900` | 截图尺寸 |
| `exePath` | 空 | `SLDWORKS.exe` 路径，留空自动查找 |
| `interopDir` | 空 | `api\redist` 目录，留空自动查找 |

例如：

```yaml
- id: dsh-solidworks
  name: '@pan-318/dsh-solidworks'
  config:
    visible: false        # 默认无界面；改成 true 才显示窗口
    autoLaunch: true
    timeoutSec: 300
```

## 实现方式

插件是一个 Host bundle：`index.js` 注册 11 个工具，`lib/runner.js` 用 Windows 自带的 `csc.exe` 把 `driver/Program.cs` 编译成驱动程序（按源码 + 互操作程序集哈希缓存），每次调用启动一个进程，通过 SolidWorks 早期绑定 COM 接口执行操作，结果以 JSON 返回。所有调用串行执行，避免多个 COM 客户端同时操作同一个 SolidWorks 实例。

## 限制

- 只支持 Windows。
- SolidWorks 没有真正的无界面内核：后台模式仍然启动 SLDWORKS.exe 进程，只是把窗口隐藏起来（`ShowWindow`）。因此任务管理器里能看到它，内存占用也和正常打开时一样；截图和 PNG 导出依赖图形窗口，所以不能用纯服务方式运行。
- 装配配合基于零件基准面（平面），不支持面/边/轴/点之间的任意配合，也不支持运动算例。
- 工程图只生成标准视图和模型尺寸，不包含剖面图、局部放大图、尺寸公差、BOM 表。
- 不支持钣金、焊件、模具、曲面编辑（曲面只支持拉伸）。
- 若 SolidWorks 弹出模态对话框（许可证、缺失引用等），该次调用会超时并返回提示。

## 许可与免责声明

- 本项目以 **MIT License** 发布，见 [LICENSE](LICENSE)。
- SOLIDWORKS 是 Dassault Systèmes SolidWorks Corporation 的商标。本项目是**非官方**社区项目，与 Dassault Systèmes 无任何关联，也未获其背书。
- 插件通过公开的 COM API 操作你本机已安装并获授权的 SolidWorks；使用前请确认你的 SolidWorks 许可允许这种自动化方式。
- 自动化会修改打开的文档。插件默认不保存，修改已保存文件前会先备份，但仍建议在重要数据上先手动备份并自行承担使用风险。





