# xingyao-perf

机械革命星耀14（`XINGYAO Series-P916F-PTL`，Core Ultra X7 358H）的极简性能模式切换工具，用来替代官方控制中心的这一项功能。

**当前状态：验证阶段，正式程序还没写。** 这里只有验证脚本和协议记录。

## 脚本

都需要在管理员 PowerShell 中运行，并会先核对主板型号。

| 脚本 | 作用 | 是否写入 |
|---|---|---|
| `scripts/probe-mode.ps1` | 读取当前模式 | 否 |
| `scripts/switch-test.ps1` | 切到办公模式，观察 15 秒后恢复 | 是 |
| `scripts/effect-test.ps1` | 三档依次满载，对比功耗和运算量，结束后恢复 | 是 |
| `scripts/thermal-test.ps1` | 满载时记录功耗、频率、温度和风扇转速；`-Load cpu|gpu|both` 选择负载；`-Mode keep` 时不写入 | 默认是 |

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\probe-mode.ps1
```

`tools/` 里是只读的分析和摸底脚本，不会发送固件指令。

**风险提示：** 写入操作属于未公开的固件接口，只在本文档记录的机型和 BIOS 上试过。不要在其他机型上去掉型号检查后运行。

## 计划

1. 确认三档的实际功耗差异（`effect-test.ps1`）。
2. 编写正式的小程序。
3. 测试停用官方服务后的重启和睡眠行为。
4. 加入监控：P / E / LPE 核频率、CPU 封装功率和温度、GPU 频率和功耗。

协议细节和验证记录见 [docs/protocol.md](docs/protocol.md)。
