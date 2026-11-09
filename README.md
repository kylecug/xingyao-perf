# xingyao-perf

机械革命星耀14（`XINGYAO Series-P916F-PTL`，Core Ultra X7 358H）的极简性能工具：托盘切换性能模式，并在任务栏上直接显示监控数值。

单个 exe，约 42 KB，依赖系统自带的 .NET Framework。

## 功能

- **模式切换：** 办公、平衡、性能。托盘图标用三根递增的条显示当前模式，切换后会回读确认。
- **任务栏监控：** 用带数值的颜色条显示 P / E / LPE 核平均频率、CPU 封装功耗和温度、GPU 频率和功耗、两个风扇转速，按列对齐。全屏应用时自动隐藏。
- **恢复模式：** 启动或唤醒后 60 秒内，把模式恢复成上次的选择。之后由 Fn 键等改动的模式会被记住。
- **开机启动：** 通过提权的计划任务实现，不弹 UAC。

右键托盘图标或监控条打开菜单。

## 构建和运行

```powershell
.\build.ps1
.\dist\xingyao-perf.exe
```

构建使用 Windows 自带的 `csc.exe`，不需要 SDK。程序需要管理员权限，没有计划任务时会弹一次 UAC。

| 参数 | 作用 |
|---|---|
| `--no-gpu` | 不读 GPU 频率，常驻内存从约 70 MB 降到约 27 MB |
| `--no-elevate` | 不请求管理员权限，模式和温度不可用 |
| `--selftest <文件>` | 读取一遍所有数据并写入文件，不做任何写入 |

设置和日志在 `%LOCALAPPDATA%\xingyao-perf`。

## 风险提示

模式切换使用未公开的固件接口，只在 BIOS 1.00 和 1.01 上放行。其他 BIOS 或机型会自动禁用切换，只保留监控。BIOS 升级后需要重新验证。

官方控制中心不知道这里的切换，它的界面可能显示旧模式。

## 其他内容

- `scripts/`：验证和测试脚本，`-Load cpu|gpu|both` 选择负载，`-Mode keep` 时不写入。
- `tools/`：只读的分析和摸底脚本。
- [docs/protocol.md](docs/protocol.md)：协议和验证记录。

## 致谢

- 性能模式协议：[faintonce/open-revo PR #166](https://github.com/faintonce/open-revo/pull/166)。
- 风扇端口协议：[Kurisu934/FanControl.MechrevoPtl](https://github.com/Kurisu934/FanControl.MechrevoPtl)（MIT）。
