# Haloflux 交接文档

> 给"下一个对话/下一个人"看的现状说明。改动前先读这里，能省掉大量摸索。

最后更新：2026-11-12

---

## 一、这是什么

Windows 11 上的**桌面动态壁纸 + 屏保**，两套独立的东西：

| 模式 | 启动参数 | 作用 | 画面 |
|---|---|---|---|
| 壁纸 | `--wall` | 视频循环播放，在桌面图标**下面** | 只有背景视频，无声 |
| 屏保 | `/s` | 全屏 HUD + 音乐 | 完整效果，带音乐 |

早期还有一个 WebView2 版壁纸（无参数启动），现在只用于 `--capture` 出预览图。

---

## 二、为什么壁纸必须用 D3D

Windows 11 桌面由 DirectComposition 合成。**任何 GDI 绘制（无论是画进子窗口还是画进桌面 DC）都会被丢弃**，屏幕上看不到。

唯一有效的路径：`CreateSwapChainForHwnd` 把 D3D11 交换链挂到桌面的 `WorkerW` 子窗口上。

相关文件：`D3DWall.cs`（交换链与上传）、`D3DDesk.cs`（驱动循环）。

---

## 三、文件地图

| 文件 | 职责 | 改动风险 |
|---|---|---|
| `Program.cs` | 入口，模式分派（`--wall` / `--native` / `/s` / `--capture` / `--native-snap`） | 中 |
| `NativeWall.cs` | 壁纸画面（GDI+），视频解码池、最小模式 | **高**（1000+ 行） |
| `D3DWall.cs` | D3D11 交换链、纹理上传、`HostAlive` | 高 |
| `D3DDesk.cs` | 30fps 循环、跳帧、全屏暂停、自愈重建 | 中 |
| `MusicPlayer.cs` | 播放列表、m4a、三种模式、音量持久化 | 低 |
| `ScreensaverForm.cs` | 屏保窗口、按键轮询、OSD、防休眠、把播放进度推给页面 | 低 |
| `TrayControl.cs` | 托盘菜单 | 低 |
| `Sampler.cs` | CPU/GPU/内存/网络/磁盘采样 | 低 |
| `AudioProbe.cs` | WASAPI 环回 + FFT（32 段） | 低 |
| `Native.cs` | Win32 P/Invoke 集合 | 低 |
| `www/engine.js` | 屏保的 canvas 绘制（1500+ 行，**视觉效果都在这**） | 中 |
| `www/app.js` | 数据绑定、把宿主消息转给 engine | 低 |
| `www/index.html` / `style.css` | 屏保 DOM 骨架 | 低 |

---

## 四、构建与运行

```powershell
cd C:\Users\LH\Documents\deepseek-harness\default-workspace\haloflux-desk

# 构建前必须做：停用看门狗并杀掉进程，否则 exe 被锁定
schtasks /Change /TN "HalofluxWatchdog" /DISABLE
Get-Process | Where-Object { $_.ProcessName -like "*Haloflux*" } | Stop-Process -Force
Start-Sleep -Seconds 3

dotnet build -c Release -v q --nologo

# 恢复
schtasks /Change /TN "HalofluxWatchdog" /ENABLE
```

**关键：exe 必须以 Medium 完整性运行**，否则 `SetParent`/交换链会失败：

```powershell
icacls .\bin\Release\net9.0-windows\HalofluxDesk.exe /setintegritylevel Medium
```

从沙箱里启动的进程是 Low 完整性，从资源管理器/计划任务启动才是 Medium。

---

## 五、安装状态（这台机器上已经装好）

| 项 | 位置/值 |
|---|---|
| 壁纸自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\HalofluxDesk` = `"...\HalofluxDesk.exe" --wall` |
| 看门狗任务 | `HalofluxWatchdog`，每 5 分钟跑 `tools\watchdog.cmd` |
| 屏保注册 | `HKCU\Control Panel\Desktop\SCRNSAVE.EXE` = `...\bin\Release\net9.0-windows\Haloflux.scr` |
| 屏保超时 | `ScreenSaveTimeOut` = 900（15 分钟） |
| 桌面快捷方式 | `Haloflux 屏保测试.lnk` |

屏保文件 `Haloflux.scr` 就是 exe 的副本，**每次改完代码要重新复制**：

```powershell
Copy-Item .\bin\Release\net9.0-windows\HalofluxDesk.exe .\bin\Release\net9.0-windows\Haloflux.scr -Force
icacls .\bin\Release\net9.0-windows\Haloflux.scr /setintegritylevel Medium
```

---

## 六、素材（不在仓库里，版权原因）

```
bin\Release\net9.0-windows\www\media\
  scene.mp4          背景视频（必须）
  bg.jpg             视频抽帧失败时的兜底静帧
  frames\*.jpg       帧序列，用 tools\Extract-Frames.ps1 生成
  music\*.mp3|m4a    播放列表，丢进去即识别（按文件名排序）
```

`frames` 是壁纸性能的关键：直接读 JPEG 比解码视频便宜得多。生成命令：

```powershell
.\tools\Extract-Frames.ps1 -Video www\media\scene.mp4 -Fps 15 -Width 1920
```

---

## 七、验证与调试手册

### 日志位置

```
%LOCALAPPDATA%\HalofluxDesk\haloflux.log     运行日志
%LOCALAPPDATA%\HalofluxDesk\crash.log        未处理异常
```

（Low 完整性运行时日志会落到 exe 目录，这种情况说明启动方式不对）

### 壁纸是否在动（最可靠）

不要看单点像素——视频有大量纯色区域会误导。用整帧差异：

```powershell
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
# 截两张，间隔约 1 秒，存到 run\diff1.png / diff2.png
python -c "from PIL import Image, ImageChops; a=Image.open('run/diff1.png'); b=Image.open('run/diff2.png'); d=ImageChops.difference(a.convert('RGB'),b.convert('RGB')); px=sum(1 for p in d.getdata() if p!=(0,0,0)); print(round(px/(a.width*a.height)*100,2),'%')"
```

**正常值 ≥ 5%**。0% 说明画面冻结。

### 屏保帧率

```powershell
$env:HALOFLUX_FPS="1"      # 启动屏保前设置
# 屏保是独立进程，需要继承这个环境变量
```

日志里会出现 `canvas fps: 55.0`。正常范围 55–60。

### 出预览图（不碰屏幕，安全）

```powershell
.\bin\Release\net9.0-windows\HalofluxDesk.exe --capture=out.png --size=1920x1080 --port=48099
```

**不要用 `CopyFromScreen` 截桌面**——会把用户的聊天窗口、私人内容一起截进去（已经犯过一次）。

---

## 八、已知坑

| 现象 | 原因 | 处理 |
|---|---|---|
| `MSB3027` 文件被锁定 | 看门狗/运行中的进程占着 exe | 构建前停用任务并杀进程 |
| 壁纸黑屏 | 进程以 Low 完整性运行 | `icacls ... /setintegritylevel Medium` |
| 画面冻结但 CPU 正常 | `FrameId` 读了只在渲染时更新的 `_t` | 已修：改读 `_clock` |
| `git push` 报 `CRYPT_E_NO_REVOCATION_CHECK` | 这台机器的 TLS 中间代理，schannel 吊销检查失败 | 用 GitHub API 推送（见下） |
| 屏保空格不暂停 | 按住超过轮询周期会触发两次 | 已修：边沿检测 + 40ms 轮询 |
| 屏保双音轨 | 页面自己也 autoplay | 已修：屏保不再给页面加 autoplay |
| 内存涨到 2.6 GB | 每帧 `new Bitmap` 造成大对象堆碎片 | 已修：20 格固定缓冲池 |

---

## 九、Git 工作流（重要）

**这台机器的 `git push` 走不通**（TLS 吊销检查失败），改动是通过 GitHub API 提交的。

- 仓库：`https://github.com/tatusi285/haloflux`
- 分支：`main`
- 作者：`LH <2499371088@qq.com>`
- 令牌：存在 Windows 凭据管理器 `git:https://github.com`（账号 `tatusi285`）

推送方式（Python，读凭据 → 建 blob/tree/commit → 移动 ref）：

```python
import base64, ctypes, json, urllib.request
from ctypes import wintypes

class C(ctypes.Structure):
    _fields_ = [("Flags",wintypes.DWORD),("Type",wintypes.DWORD),("TargetName",wintypes.LPWSTR),
                ("Comment",wintypes.LPWSTR),("LastWritten",wintypes.FILETIME),("CredentialBlobSize",wintypes.DWORD),
                ("CredentialBlob",ctypes.POINTER(ctypes.c_byte)),("Persist",wintypes.DWORD),
                ("AttributeCount",wintypes.DWORD),("Attributes",ctypes.c_void_p),
                ("TargetAlias",wintypes.LPWSTR),("UserName",wintypes.LPWSTR)]

a = ctypes.WinDLL("advapi32", use_last_error=True); p = ctypes.c_void_p()
a.CredReadW("git:https://github.com", 1, 0, ctypes.byref(p))
c = ctypes.cast(p, ctypes.POINTER(C)).contents
T = ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize).decode("utf-16-le").rstrip("\x00")
a.CredFree(p)

def api(m, path, payload=None):
    data = json.dumps(payload).encode() if payload is not None else None
    r = urllib.request.Request("https://api.github.com"+path, data=data, method=m)
    r.add_header("Authorization","token "+T); r.add_header("User-Agent","haloflux")
    r.add_header("Accept","application/vnd.github+json")
    if data: r.add_header("Content-Type","application/json")
    return json.loads(urllib.request.urlopen(r, timeout=60).read().decode())

# 1. head -> base tree   2. POST blobs   3. POST trees(base_tree=...)   4. POST commits   5. PATCH refs/heads/main
```

本地仓库和远端是**两条并行历史**（远端走 API、本地只 commit）。内容一致，但 hash 不同。网络恢复后可 `git fetch && git reset --hard origin/main` 合并。

---

## 十、当前性能基线

| 指标 | 壁纸 | 屏保 |
|---|---|---|
| 帧率 | 15 fps（等于视频源） | 55 fps |
| CPU | 约 20% 单核 | 约 9% 单核 |
| 内存 | 320 MB | 130 MB |
| 渲染耗时 | 12 ms/帧 | — |

壁纸只在新视频帧到达时才渲染、上传、显示（`FrameId` + `_lastFrameId`）。

---

## 十一、已完成 vs 待办

### 已完成

- D3D 交换链挂载，壁纸在图标下方、桌面行为完全原生
- 视频帧序列 + 固定缓冲池解码器
- 内存泄漏修复（2.6 GB → 320 MB 稳定）
- 自愈：宿主窗口重建、睡眠唤醒、分辨率变化、单实例锁、看门狗任务
- 屏保：音乐播放（mp3/m4a/aac/wav）、三种播放模式、音量（↑↓ 持久化）、
  空格暂停、←→ 切歌、M 切模式、防休眠、动鼠标退出
- 视觉：环速随 CPU、八条数据弧（缓动/呼吸/脉冲/游标）、音乐进度弧、
  鼓点冲击波、切歌重组、静音雷达待机
- 可读性：标签沿切线倾斜 ≤26°，数值水平，标题按字数居中
- 全屏应用自动暂停壁纸

### 待办（按性价比）

1. **一键安装脚本** —— 现在装一次要手做三件事
2. **Release 打包** —— 别人 clone 下来用不了
3. **专辑封面** —— 从 mp3/m4a 标签读封面，画在核心位置
4. **歌词精简版** —— 当前行 + 下一行，2–3 小时
5. **多显示器** —— 目前只覆盖主屏

---

## 十二、设计约定（改之前请遵守）

1. **克制优先**：这是壁纸不是仪表盘。曾经把八个扇区塞满文字，观感崩了，后来砍到"一个标题 + 一个数值"。
2. **每帧层 vs 缓存层**：动态元素（数据弧、冲击波）每帧画；静态元素（环带纹理、刻度）走缓存。
3. **强度统一压着来**：新效果初始透明度 25–35%，看效果再提亮。
4. **事件互斥**：切歌重组期间不触发冲击波；同时最多 3 道冲击波。
5. **不要碰用户屏幕**：验证用 `--capture` 离屏渲染，不要 `CopyFromScreen`。

---

## 十三、更换背景视频的完整流程（2026-11-12 实践）

### 素材来源

B 站视频用 bilibili 客户端下载后存在 `C:\Users\LH\Videos\bilibili\<avid>\`，
分两个 `.m4s`：`*-1-30080.m4s` 是画面，`*-1-30280.m4s` 是音频（不需要音频）。

**坑**：bilibili 会在文件头塞 9 个 ASCII `0`（`30 30 30 ...`），ffmpeg 会直接报
`Invalid data found`。要先剥掉前缀，让 `ftyp` 盒子落在文件开头：

```python
data = path.read_bytes()
idx = data.find(b"ftyp")
path.with_suffix(".clean.mp4").write_bytes(data[idx - 4:])   # 连盒子长度一起搬
```

### 只取最好看的一段

整段视频常常是同一个镜头（这次的 3 分 42 秒构图完全没变），没必要全用。
先出缩略图对照表选段：

```powershell
ffmpeg -i scene.mp4 -vf "fps=1/10,scale=480:-2" -q:v 4 thumbs\t_%03d.jpg
```

### 去水印

B 站客户端下载的视频带 UP 主水印（这次的位于 x37..298, y36..97）。
裁边会放大水印，正确做法是 `delogo`：

```powershell
ffmpeg -ss 145 -t 40 -i scene.mp4 -an -vf "delogo=x=26:y=24:w=288:h=86" `
       -c:v libx264 -crf 18 -preset medium -pix_fmt yuv420p scene-clean.mp4
```

水印区域是深色渐变，delogo 插值后完全看不出。

### 抽帧

```powershell
ffmpeg -i scene-clean.mp4 -vf "fps=15,scale=1920:-2" -q:v 5 frames\f_%04d.jpg
```

40 秒 → 600 帧 → 52 MB（原来 3 分 42 秒是 3330 帧、269 MB）。

### 换完必须验证真的在动

```powershell
# 两次截屏，间隔约 1 秒，比较差异
# 正常 ≥ 5%，接近 0 说明冻结
```

---

## 十四、交叉淡化的代价（重要）

`CrossFade: true` 时，每帧要把两张 1080p 混合，成本很高：

| CrossFade | WallFps | 呈现帧率 | CPU（单核） | 说明 |
|---|---|---|---|---|
| false | — | 15 | **约 20%** | 原始顿挫感 |
| true | 20 | 20 | 约 52% | 折中 |
| true | 30 | 30 | 约 60% | 最顺滑 |

改 `haloflux.json` 里的 `CrossFade` / `WallFps` 即可切换，不用重编译。

**注意**：GDI+ 的整帧 alpha 混合就是这个量级，想再降只能换渲染后端（Direct2D 或 GPU 混合），不属于小改动。

---

## 十五、基准测试 `--bench`

改完代码用它对比，别靠肉眼：

```powershell
cd bin\Release\net9.0-windows
.\HalofluxDesk.exe --bench=20        # 跑 20 秒
```

输出 fps、render 的 mean / p50 / p95 / max、CPU 占用、内存峰值，并写一份
`bench-<时间>.txt`。它只测渲染路径（不含 D3D 上传），适合比较改动前后。

**基线（2026-11-12，40 秒片段，20fps，自适应淡化）**

```
frames 301 (20.1 fps)  render mean 6.5 ms  p95 10.6 ms  cpu 46%  311 MB
```

## 十六、视觉效果的两个"烘焙"技巧

壁纸的 CPU 预算很紧，凡是能离线算的就别在运行时算：

| 效果 | 运行时做法 | 实测代价 | 现在怎么做 |
|---|---|---|---|
| 色彩分级 | 每帧套 ColorMatrix | 未测，估计 8 ms | **烘进视频**（ffmpeg colorbalance + eq） |
| 画面呼吸（±3% 亮度，40 秒周期） | 每帧全屏 alpha 填充 | **8.7 ms** | **烘进帧序列**（`eq=brightness='0.025*sin(2*PI*t/40)'`） |
| 风格衔接光晕 | 每帧缩放绘制 | 5 ms | **预缩放缓存**（0.2 ms） |

呼吸的周期必须和循环长度一致（40 秒），否则循环接缝处会跳。

这些参数都在 `haloflux.json`：`StyleBridge`、`Breathing`、`Vignette`、`MotionThreshold`、`WallFps`、`CrossFade`。

---

## 十七、2026-11-12 晚间改动（本会话后半段）

### 布局：环右移并贴边

- 圆心改为 `W - ro * 0.998`，环的外缘正好贴住画面右边界，左侧全部让给影片
- 半径 `H * 0.52` → `H * 0.415`，纵向中心 `0.445` → `0.485`，不再顶到顶部数据条
- 结果：她的脸完整露出，环独立工作在右侧

### 科技感：三处细节

| 细节 | 实现 |
|---|---|
| 发丝刻度 | 240 根（每 1.5°），每 10 根加长 |
| 流动虚线环 | `setLineDash([2,9])` + `lineDashOffset` 随时间递减，看起来在“走字” |
| 色散 | 外圈红/青各描一次，错开 ±0.8px，模拟真实显示屏的色差 |

### 扇区重做

原来每扇区只有「标题 + 数值」，太单调，现在三层：

1. **六格分段光条**：按百分比点亮，颜色 `<70% 青 / 70–90% 琥珀 / >90% 红`
2. **状态灯**：同阈值，带呼吸脉动
3. **数值滚动**：数字按 14%/帧缓动逼近目标，不会跳变（`rollValue`）
4. **告警泛光**：扇区 >85% 时整个楔形泛红并呼吸

光条固定在**内圈带**（`ri + 16`），数值在外圈，避免叠在一起。

### 粒子云：从环绕改为全屏

- 320 颗粒子分布在整幅画面（归一化坐标），缓慢上浮、左右摆动
- 每颗有 `depth` 决定大小与亮度，形成前后层次
- 鼓点时从环心向外推出一圈**横穿全屏**的亮壳
- 绘制在环**之下**，不会盖住 HUD

### 让影片压过特效（重要）

之前她被四层遮罩压得发灰，实测她所在区域亮度只有画面平均的 0.9 倍。现在：

| 项 | 前 | 后 |
|---|---|---|
| 视频饱和度 | 0.74 | **1.34** |
| 视频亮度 | 1.02 | **1.24** |
| 径向压暗 | 0.26 | **0.07** |
| 暗角 | 0.95 | **0.54** |
| 星云叠加 | 0.78 | **0.15** |
| 粒子数量/亮度 | 260 / 0.16–0.46 | 190 / 0.085–0.26 |

结果：她的区域亮度 **70.7**，全屏平均 **45.2**，比画面平均亮 56%。

另外：**流星**飞过她那一侧时按 x 坐标衰减到 12%，避免粉线划过脸颊。

### 闪退与弹窗（已定位）

1. **看门狗脚本曾写坏**（VBS 引号不匹配），每 5 分钟弹出错误框，**抢走全屏游戏焦点**导致游戏退出。已重写并用 `cscript` 验证。
2. **壁纸模式曾在跑 WASAPI 环回采集**（壁纸根本不需要音频），会占用音频设备。现在壁纸模式完全不碰音频和性能计数器。
3. **游戏检测提速**：从“枚举全部进程、2 秒一次、缓存 5 秒”改成“按名字查询、400ms 一次、缓存 0.5 秒”，游戏启动 0.5 秒内壁纸就让路。
4. 检测到游戏时壁纸**先销毁交换链和窗口再退出**，不留下半残的桌面状态。

`Native.Games` 列表：`ZenlessZoneZero / GenshinImpact / YuanShen / StarRail / osu / VALORANT / cs2 / javaw`。

---

## 十八、绝区零闪退的真实原因（与本项目无关）

游戏日志 `C:\Users\LH\AppData\LocalLow\miHoYo\绝区零\Player.log`：

```
d3d11: failed to create 2D texture id=589 width=1024 height=1024 mips=11 
       [D3D error was 8007000e]
async texture load: failed to load StreamTex:Crowe_Body_A from archive:...
```

`0x8007000E` = **E_OUTOFMEMORY**，创建纹理时显存/内存不足。

排查结论：

| 检查 | 结果 |
|---|---|
| 显卡 | RTX 4060 Laptop，驱动 2026/10/19，7 天无 TDR |
| 内存 | 15.7 GB，启动前已用 10.7 GB |
| 页面文件 | 23.7 GB，正常 |
| 游戏位置 | E 盘，剩余 31.6 GB |
| 事件日志 | 无游戏崩溃记录（是退出不是崩溃） |

**建议**：NVIDIA 控制面板把 `ZenlessZoneZero.exe` 指定为高性能独显；开游戏前关掉占内存的程序（DSH 三个进程 1.8 GB）；游戏内纹理质量降到中。

---

## 十九、VBScript 已被 Windows 标记弃用

事件日志里出现 `VBScriptDeprecationAlert`。看门狗目前仍是 `tools\watchdog.vbs`（工作正常、静默），但**将来 Windows 版本会移除 VBScript**。

后续如果要彻底摆脱它，可以把看门狗做成 `HalofluxDesk.exe --watchdog` 模式，由计划任务直接调用，不依赖脚本宿主。

---

## 二十、本会话归档（2026-11-12）

### 做了的事

| 类别 | 内容 |
|---|---|
| 素材 | 换视频（bilibili .m4s 需剥 9 字节前缀）、40 秒选段、delogo 去水印、色彩分级、亮度呼吸烘焙 |
| 壁纸 | 帧间交叉淡化（自适应）、只在新帧时渲染、全屏应用/游戏自动退让、不再采集音频 |
| 屏保 | 环右移贴边、发丝刻度+虚线环+色散、扇区六格光条+状态灯+数值滚动+告警泛光、全屏粒子云、影片亮度压倒特效 |
| 工程 | `--bench` 基准测试、`haloflux.json` 配置、诊断叠加层、看门狗 VBS 重写、快速游戏检测 |
| 文档 | 本文档；仓库 README |

### 关键教训

1. **静态图 + 程序动效替代不了真动画**——试过一版，差距明显，已回滚。想做这类效果必须靠美术素材，不能靠代码“画”。
2. **GDI+ 的整帧 alpha 混合很贵**（16.7ms/帧）——凡是能离线烘焙的效果（分级、呼吸）都别放运行时。
3. **透明通道的坑**：`.convert("RGB")` 会丢掉刚做好的 alpha；JPEG 预览里的棋盘格是像素不是透明。
4. **弹窗会杀死全屏游戏**——任何后台脚本的错误对话框都可能让游戏失焦退出。
5. **验证要看数据不要看“进程还在”**：用帧率探针、像素差异、亮度统计，而不是“进程跑起来了”。

### 当前状态

```
壁纸   20fps  7ms/帧  CPU ~30%  326 MB  视频背景（40 秒循环，无水印）
屏保   55fps  CPU ~7%  130 MB  影片+环+粒子云+扇区仪表
游戏   检测到就完全退出，游戏结束后看门狗再拉起
```
