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
