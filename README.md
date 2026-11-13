# 麻将出牌助手 · C# 重构版（自用）

原程序是 Python 3.13 + tkinter + OpenCV + ONNX Runtime，PyInstaller 打包成 215MB 的发行包，
且不含源码。本仓库从 exe 里的 `.pyc` 还原逻辑，用 .NET 8 重写，**复用原 `weights.onnx`**，
不重新训练、不改变识别模型。

## 快速开始

已发布版本（免安装，需 .NET 8 桌面运行时，本机已有 8.0.34）：

```
publish\win\MahjongAssistant.exe
```

从源码跑：

```
dotnet build src/Mahjong.Desktop/Mahjong.Desktop.csproj --no-restore
src\Mahjong.Desktop\bin\Debug\net8.0-windows\MahjongAssistant.exe
```

用法：打开牌局窗口并保持**可见、非最小化**，程序会自己找到它并开始读取，右侧悬浮卡显示
建议出牌、向听数、进张数与点炮粗估。不需要任何点击。

**`Ctrl+Shift+H`（或界面上的按钮）可随时隐藏/显示悬浮卡**，屏幕共享或录屏时不会误露。

## 识别优化

原版每帧只跑一次整屏推理。本版加了一条**多档阶梯**，默认只在"结果不够用"时才多跑：

| 档 | 触发条件 | 做什么 |
|---|---|---|
| `full` | 每帧 | 整屏推理（与原版完全相同） |
| `full-low` | 手牌不是 13/14 张 | 用 `lowConfidence`（0.12）重跑整屏，捡回低分牌 |
| `roi` | 仍不够，且牌面偏小 | 裁出手牌带再推理，把牌面放大到模型舒适区 |
| `roi-low` | 仍不够，且张数 < 13 | 裁剪 + 低阈值 |

选择规则：`手牌张数 + 4 × 平均置信度`，13/14 张的档位永远压过其他档位。所以这条阶梯
**只能更好，不会更差**——最坏情况是多花一次推理（约 120ms），不影响结果。

### 模型其实怕"牌太大"

这是本轮最反直觉的发现。用 `Mahjong.Smoke scan` 对同一帧扫不同缩放：

```
frame 1280x720, 12 tiles, median tile 46px, row centre 635,642
  scale 0.500  roi 1280x720   tile~ 22.9px  ->  8 tiles
  scale 0.550  roi 1164x720   tile~ 25.1px  ->  6 tiles
  scale 0.650  roi  985x720   tile~ 29.7px  ->  5 tiles
  scale 0.750  roi  853x720   tile~ 34.3px  ->  0 tiles
  scale 0.900  roi  711x711   tile~ 41.2px  ->  0 tiles
```

检测数随牌面变大**单调下降**，34px 以上直接归零。所以"把牌裁出来放大"这种直觉做法在这个
模型上是有害的。正确做法是**缩放到它训练时的尺寸**（约 22px）：`targetTilePixels` 就是干这个的，
牌已经够大就完全不裁。

### 防抖

`handStableFrames: 2`：首个结果立刻显示，但变化后的手牌要连续出现 2 帧才替换，避免单帧误读
导致建议乱跳。识别为空时立即清空，不会留旧建议。

### 静止跳过（省 CPU）

一副手牌在整局里大部分时间是静止的，而一次推理要 150–800ms。`staticSkip: true` 会在**上次识别
区域内画面像素没变**时直接复用上一次结果，跳过整轮推理：

| 项 | 实测 |
|---|---|
| 变化判定耗时 | **1.3 ms / 帧**（48×48 灰度网格比对） |
| 被跳过的推理 | 152–279 ms / 次 |
| 静止时的节省 | 约 **96%** 的推理开销 |

保守设计，不会把你带偏：

- 只比对**上次识别到手牌的那一条带**，别处（倒计时、聊天区）闪动不算变化；
- 手牌还在防抖等待确认时**不跳过**，否则变化永远确认不了；
- 连续跳过 `staticSkipMax: 20` 帧后强制推理一次，防止缓慢漂移被永久忽略；
- 想关掉：`staticSkip: false`。

日志里会各写一行 `static: ...` / `resumed: ...`，能直接看出跳过从哪一帧开始。

## 悬浮卡跟随与高 DPI

`followWindow: true`：牌桌窗口被拖动或缩放时，悬浮卡自动跟着走，不会留在原地。

定位换算也修了一个真问题：Win32 报告的是**物理像素**，而 WPF 的 `Left/Top` 是**设备无关单位**。
两者在高缩放比下差一倍以上——本机实测 DPI 为 **168（175%）**，旧写法会把卡片放到 x≈2900px，
直接飞出屏幕。现在按目标窗口所在显示器的 DPI 换算，并夹在该显示器工作区内，右侧放不下就翻到左侧。
换算逻辑抽成纯函数 `OverlayGeometry.Beside`，有单测覆盖 100%、175%、贴右边翻边三种情况。

## 自动模式

默认开启（`autoStart: true`）。启动后的行为：

1. 加载模型 → 按关键字扫描可见窗口 → 选中匹配项 → 自动开始读屏 → 弹出悬浮建议卡。
2. 帧循环里每秒检查目标窗口：**关闭或最小化就暂停**，并每 3 秒重扫一次，窗口回来后自动继续。
3. 点「停止」会记住这次是人为停止，不再自动重连；再点「开始」或重新勾选自动模式即可恢复。
4. 取消勾选「自动模式」即退回手动：自己在列表里选窗口、点开始。

匹配关键字（标题或进程名，命中即算，取客户区最大者）：

```
微乐 / 麻将 / 微信 / wechat / mahjong / mumu / 模拟器 / 夜神 / 雷电
```

按你的实际环境，MuMu 模拟器里跑小程序麻将也能直接命中（实测 `MuMu安卓设备 / MuMuNxDevice`）。
前提是模拟器窗口本身没被遮挡——GDI 截屏抓的是屏幕像素，挡住的窗口截不到。

改关键字：编辑 `appsettings.json` 的 `windowKeywords`，重启生效。
不清扫哪些窗口：跑 `Mahjong.Smoke windows` 会列出所有可见窗口，并标出 `*` 命中的那个和最终选择，
这是排查"为什么没自动开始"最快的办法。

```
dotnet run --project src/Mahjong.Smoke -- windows
```

## 诊断日志

每次运行都会追加到 `logs/mahjong-assistant.log`（相对 exe 目录，超过 1MB 自动清空）：

```
16:00:12 === session start ===
16:00:12 hotkey registered: Ctrl+Shift+H toggles the card
16:00:12 model loaded: ...\assets/weights.onnx
16:00:13 auto: picked MuMu安卓设备 / MuMuNxDevice 768x1435
16:00:13 start reading: MuMu安卓设备 hwnd=0xB0E04
16:00:14 画面 768×1435 · 检测 4 张 · 手牌 2 张（上边） · 边缘 上2/下0/左0/右3 · full 154ms/1次
```

识别不准时把这几行贴出来即可定位：`检测 N 张` 说明模型看到了什么，`手牌 X 张（某边）`
说明行选择落在哪条边，`边缘` 四个数是四条带的候选数量，末尾 `full 154ms/1次` 是赢下的档位与
实际跑了几次推理（`1次` 表示一次就够，没多花钱）。

### 四个自检命令

```
dotnet run --project src/Mahjong.Smoke -- windows          # 谁能被自动匹配
dotnet run --project src/Mahjong.Smoke -- capture 输出目录   # 存一帧 + 标注图 + 逐档明细
dotnet run --project src/Mahjong.Smoke -- scan 某帧.png      # 扫描该帧的最佳缩放
dotnet run --project src/Mahjong.Smoke -- static 某帧.png    # 静止跳过省了多少
```

`capture` 会把原始帧和画了检测框的 `annotated_*.png` 都存下来，是排查识别问题最直接的材料；
`scan` 用来判断某台机器/某个分辨率下该把 `targetTilePixels` 设成多少；`static` 对比帧差判定
与推理的耗时，验证省电效果。

## 和原版的一致性

行为不是"凭描述重写"，而是拿原版 `.pyc` 里跑出来的输出做对拍：

| 项目 | 用例数 | 结果 |
|---|---|---|
| 向听数 / 胡牌 / 听牌 / 进张 / 弃牌排序 / 风险档位 | 50 | 全部一致 |
| 手牌行选择 `choose_hand`（4 边带状筛选 + 聚类 + 缺口分段） | 120 | 全部一致 |
| 本轮新增（档位择优 / 裁剪规则 / 防抖 / ROI 坐标等价） | 14 | 全绿 |
| 交互逻辑（静止跳过 / 强制刷新 / 防抖待定 / 悬浮卡定位与 DPI 换算） | 20 | 全绿 |
| ONNX 推理链路（模型加载、前处理、NMS、坐标还原） | 冒烟 | 120 ms/帧，框位与合成图一致 |

```
dotnet test src/Mahjong.Tests/Mahjong.Tests.csproj --no-restore
# 已通过! - 失败: 0，通过: 184，总计: 184
```

向量由 `tools/make_golden.py`、`tools/make_row_vectors.py` 直接驱动原版 code object 生成，
不是手写期望值。原版反汇编保留在 `reverse/dis/` 作为移植依据。

## 尚未移植的原版功能

原版 `desktop_assistant` 还有几块逻辑没搬，属于"牌局状态判断"层，不影响单帧识别与建议：

- 自摸按钮检测（橙色 HSV 阈值）与"杠 ×4 待选择"暂停建议
- 三家牌河提取 `detect_opponent_rivers`（目前点炮粗估用的是全量可见牌，偏保守）
- 副露登记（碰/吃/杠）与牌数一致性校验；因此当前只认 13 或 14 张手牌
- 牌墙余数七段数码管识别 `recognize_wall_remaining`
- 复盘记录 `TrainingJournal`（本机写盘）
- 孤张优先级细则（孤风 → 孤箭 → 缺门孤张 → 幺九 → 二八，三七尽量留）

这些是下一步的增量，接进来时同样会用原版输出做对拍。

## 待实机校准

识别参数是对着原版固定分辨率调死的，换截图后端后需要一轮实测：

| 参数 | 位置 | 现值 |
|---|---|---|
| 置信度 / NMS | `appsettings.json` | 0.25 / 0.45 |
| 兜底置信度 | `appsettings.json` `lowConfidence` | 0.12 |
| 目标牌面像素 | `appsettings.json` `targetTilePixels` | 22（`scan` 实测最佳） |
| 多档补跑 / 裁剪补跑 | `appsettings.json` | `deepRetry` / `adaptiveRefine` 均 true |
| 防抖帧数 | `appsettings.json` `handStableFrames` | 2（1 = 关闭） |
| 读取间隔 | `appsettings.json` `intervalMs` | 1000 |
| 静止跳过 / 强制刷新间隔 | `appsettings.json` | `staticSkip` true / `staticSkipMax` 20 |
| 悬浮卡跟随窗口 | `appsettings.json` `followWindow` | true |
| 自动目标关键字 | `appsettings.json` `windowKeywords` | 9 个（含模拟器） |
| 重扫间隔 | `appsettings.json` `autoRescanMs` | 3000 |
| 边缘带宽 | `HandRowSelector.Band` | 0.2 |
| 聚类容差 | `HandRowSelector.ToleranceRatio` | 0.055（按轴长） |
| 缺口分段系数 | `HandRowSelector.GapFactor` | 1.8（中位牌宽） |

另需注意：原版用 PIL `ImageGrab` 抓屏，本版用 GDI `CopyFromScreen`，**窗口被遮挡就抓不到**
（与原版同样的限制）。高 DPI 缩放若导致坐标偏移，在 exe 属性里设"替代高 DPI 缩放行为"。

## 项目结构

```
src/Mahjong.Core      牌编码、向听/胡牌/进张、弃牌建议（纯逻辑，无 IO）
src/Mahjong.Vision    Win32 窗口枚举、GDI 截屏、ONNX YOLO 42 类检测、手牌行选择、多档分析、防抖
src/Mahjong.Desktop   WPF 主窗 + 置顶悬浮建议卡 + 全局热键
src/Mahjong.Smoke     无界面自检：推理耗时、窗口清单、帧抓取与标注、缩放扫描
src/Mahjong.Tests     xunit 对拍（204 例：50 逻辑 + 120 行选择 + 14 优化 + 20 交互）
tools/                从 exe 提取 pyc、生成黄金向量、生成图标的脚本
tools/icons/          图标源 SVG（iconoven/mahjong-tile，MIT）
reverse/              反汇编、pyc、黄金向量（移植依据）
logs/                 运行时诊断日志（自动创建）
```

## 图标与字体

- 图标图形取自开源图标集 **IconOven 的 mahjong-tile**（MIT 许可，经 Iconify 获取），
  仅做配色与背景处理；许可全文见 `assets/icons/NOTICE.md`。重新生成：
  `python tools/rasterize_icon.py && python tools/make_icon.py`。
- 界面字体使用系统自带 微软雅黑 UI，未嵌入任何字体文件。

## 离线构建

首次还原后，本地 NuGet 缓存已包含全部依赖，后续构建无需联网：

```
dotnet build  ... --no-restore
dotnet test   ... --no-restore
dotnet publish src/Mahjong.Desktop -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win
```

发布产物体积 24MB（13MB 单文件 exe + 12MB 模型），对比原版 215MB。

## 说明

- 仅供本机自用，只读取屏幕、不操作游戏进程。
- 随附的 `weights.onnx` 来自原发行包，其权重授权未明（原 README 也写明"不要公开分发"）。
  自用没问题；若要对外分发，需先解决授权或自行训练替代模型。
- 识别与概率均为估算，点炮区间未经牌谱校准，不构成胜负保证。
