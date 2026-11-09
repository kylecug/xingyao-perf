# 协议和验证记录

## 性能模式接口

`root\WMI` → `OemWMIMethod.OemWMIfun`，输入 64 字节（不足补 0），输出 256 字节，需要管理员权限。

| 操作 | 输入 | 成功时的返回 |
|---|---|---|
| 读取模式 | `[3, 9]` | `byte[0]=0`，`byte[1]` 为模式 |
| 办公 | `[3, 10, 1]` | `byte[0]=0x94` |
| 平衡 | `[3, 10, 2]` | `byte[0]=0x95` |
| 性能 | `[3, 10, 3]` | `byte[0]=0x96` |

模式值：1 办公，2 平衡，3 性能。官方配置文件用的是另一套编号：`OperatingMode` 0 办公，1 平衡（代码里叫 Gaming），2 性能（Turbo）。

协议来源：[faintonce/open-revo PR #166](https://github.com/faintonce/open-revo/pull/166)，该 PR 只验证了 BIOS 1.00。

## 本机验证（BIOS 1.01，控制中心 5.57.1.2）

已通过，2026-11-09：

- 读取模式，结果与官方界面一致。
- 平衡→办公→平衡，返回值正确，回读 6–10 ms 内一致。官方服务运行时，15 秒内没有把模式改回去。
- 切到性能模式，返回值和回读正确。

首次满载对比（`effect-test.ps1`，每档 75 秒，持续值取最后 30 秒）：

| 模式 | 峰值 W | 持续 W | 持续 MHz | 运算量 |
|---|---:|---:|---:|---:|
| 平衡 | 36.7 | 31.9 | 3639 | 89136 |
| 办公 | 33.2 | 24.4 | 3380 | 83294 |
| 性能 | 35.2 | 31.5 | 3642 | 89981 |

办公模式的功耗确实下降。只跑 CPU 时性能与平衡基本相同，频率也几乎一样。

CPU + 核显同时满载（`thermal-test.ps1 -Load both`，每档 150 秒，持续值取最后 30 秒，开着 GamePP）：

| 模式 | 峰值 W | 高功耗保持时长 | 持续 W | 持续核显 W | 持续 MHz | 峰值 °C | 持续 °C | 持续风扇 RPM |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 办公 | 45.5 | 约 20 秒 | 22.0 | 12.5 | 1950 | 73 | 55 | 4913 / 4697 |
| 平衡 | 46.2 | 约 40 秒 | 33.8 | 19.0 | 2532 | 77 | 63 | 6196 / 5989 |
| 性能 | 46.1 | 约 60 秒 | 39.9 | 22.4 | 2732 | 78 | 69 | 6506 / 6282 |

结论：直连切换到性能模式有效。持续功耗高约 6 W，温度远未到上限，`Performance Limit Flags` 全程为 0。只跑 CPU 测不出差别，可能是 CPU 频率先到顶，这点尚未证实。

## 电池供电下的双烤（2026-11-09）

`thermal-test.ps1 -Mode office,balanced,turbo -Load both -OnBattery -CoolSeconds 45 -LoadSeconds 240`，官方后台已停用，开着 GamePP，每档只跑一次，顺序固定。结果在 `scripts/thermal-result-office-balanced-turbo-both-battery.txt`。

| 模式 | 高功耗阶段 | 持续封装 W（约 60–160 秒） | 持续电池 W（约 60–160 秒） | 最后 30 秒封装 W | 峰值 °C | 持续风扇 RPM |
|---|---|---:|---:|---:|---:|---:|
| 办公 | 约 33 W，约 50 秒 | 约 21 | 约 28 | 25.3 | 72 | 4900 / 4711 |
| 平衡 | 约 33 W，约 45 秒 | 约 22 | 约 29 | 22.6 | 66 | 4692 / 4485 |
| 性能 | 约 33 W，约 45 秒 | 约 23 | 约 31 | 23.0 | 66 | 4701 / 4488 |

三档差别很小，远小于插电时（持续 22.0 / 33.8 / 39.9 W）。电池放电峰值约 42 W。持续阶段只有 55–57°C，`Performance Limit Flags` 全程为 0，所以限制不是温度造成的，很可能是电池供电的功耗上限，具体限制来源未查明。办公模式在约 167 秒后从 21 W 升到 25–26 W，原因未查明，因此它的最后 30 秒数据偏高。

## 停用官方后台后的验证（2026-11-09）

`vendor-service.ps1 -Action disable` 后：

- 用户确认正常：模式切换、监控数值、Fn 键、睡眠唤醒后模式保持。
- 完整重启（启动类型 0x0）后，由日志和状态确认：`GCUBridge` 保持停用，`GCUService` 和托盘未运行；登录任务启动了程序，保存的模式未变（办公），没有恢复或改动记录，风扇和 GPU 没有错误记录。
- 重启时温度读取首次报 `Invalid object`（程序启动后约 5 秒）。程序暂停 60 秒后重试，之后没有再报错。
- 充电阈值读数在停用前、停用后、重启后都是 `enabled=0, lower=0, upper=0`。

待验证：

- 放电后回充时停在哪里。接显示器的 USB-C 供电也可能影响结果。

## 风扇（PM2 端口）

转速和模式读取已在本机验证：负载中连续采样无错误，EC 读到的模式与 WMI 一致，转速随负载上升。风扇写入未在本机测试。

来源：[Kurisu934/FanControl.MechrevoPtl](https://github.com/Kurisu934/FanControl.MechrevoPtl)（MIT），在 P916F-PTL BIOS 1.01 / EC 01.01.00 上实测。依赖现有 `inpoutx64` 驱动，SHA-256 `F8965FDC…F221B`，本机一致。

端口：命令/状态 `0x6C`，数据 `0x68`；IBF 掩码 2，OBF 掩码 1。

| 操作 | 命令 | 参数 |
|---|---|---|
| 读风扇 0 低/高字节 | `D5` | `18` / `19` |
| 读风扇 1 低/高字节 | `D5` | `16` / `17` |
| 读散热模式 | `E7` | `11` |
| 恢复自动 | `D5` | `10` |
| 两风扇共用 PWM | `D6` | `round(percent * 255 / 100)` |
| 风扇 0 / 1 目标转速 | `D7` / `D8` | `round(rpm / 100)` |

注意事项：

- RPM 分两个字节读取，不是原子的，需要“高-低-高”校验。
- 手动 PWM 绕过 EC 的温度曲线。进程崩溃时不保证恢复自动，需要自己做保护。
- 互斥锁 `Local\MechrevoPtlFanDiagnostic` 只协调遵守它的程序，官方软件不遵守。
- 该项目文档称三档各有独立风扇曲线，最高目标分别约 4900/4700、6500/6300、7100/7000 RPM。

## 电池充电上限（未在本机验证）

本机只发送过读取命令，写入命令没有发送过，不能直接使用。

本机读取记录（BIOS 1.01，官方服务运行中，`HealthProtectionStatus=1`），2026-11-09：`enabled=0, lower=0, upper=0`，电量 97%，插电，未充电，充电功率 0。三条命令都有返回，握手正常。全 0 与 open-revo 写入的“关闭”状态相同，但也可能表示这组命令在 1.01 上没有实际作用，尚未区分。这次读数也说明，此时的未充电不是这组阈值造成的，原因未查明。

来源：[clmslgs/open-revo](https://github.com/clmslgs/open-revo) `ptl_pmc.rs`，提交 948ca76（PR #166）。该 PR 只验证了 BIOS 1.00，页面没有写是否测过重启后保持。端口和 PM2 风扇相同。

| 操作 | 命令 | 参数 | 返回 |
|---|---|---|---|
| 读启用标志 | `F1` | `12` | 0 或 1 |
| 读下阈值（停止充电） | `F1` | `13` | 百分比 |
| 读上阈值（主动放电） | `F1` | `14` | 百分比 |
| 关闭 / 启用 | `F1` | `10` / `11` | 无 |
| 设下阈值 | `F2` | 百分比 | 回显 |
| 设上阈值 | `F3` | 百分比 | 回显 |

open-revo 的写入顺序：关闭 → 设上阈值 → 设下阈值 → 启用 → 回读，失败时写回原状态。60% 和 80% 写成 `enabled=1, lower=限制值, upper=100`。100% 写成 `enabled=0, lower=0, upper=0`，而它的文档和注释都说上阈值保持 100%，两者不一致，写 0 的影响未知。

官方服务里的线索：`BatteryProtection2`（本机日志显示启用的是它）有上下限读写方法，并响应电量变化和电源事件，方法体被混淆，看不出是否在后台维持设置。`Define.ECSpec` 里有 `ADDR_BATTERY_CHARGE_LIMIT_UP = 0x7B9` 和 `DOWN = 0x7D0`。注册表 `HKLM\SOFTWARE\OEM\GamingCenter2\BatteryProtection2` 的 `HealthProtectionStatus` 当前为 1，枚举为 0 性能、1 平衡、2 健康。

不要用于本机的方案：[Leo-stro/mechrevo-charge-limit](https://github.com/Leo-stro/mechrevo-charge-limit) 的 EC 写入序列（`0x07B9`、`0x07C3`）。它只在无界15X 上测过，其 README 也说明不适用于星耀14 STX。

只读探测：`scripts/battery-read.ps1 -Label <名称>`，只能发送 `F1 12/13/14`，结果追加到 `%LOCALAPPDATA%\xingyao-perf\battery-read.log`。

## 已知行为

- 直接写固件时官方服务不知情，其界面和 `MainOption.json` 不会更新，显示可能与实际模式不一致。
- 官方链路：界面或托盘 → 本机 MQTT 代理 `GCUBridge`（端口 13688）→ `GCUService` → 固件。`GCUService` 由 `GCUBridge` 启动，不是独立的 Windows 服务。
- `GCUService` 可能停止运行。此时官方界面的模式全空，点击后变灰。曾遇到一次，重启后恢复，原因未查明。

## 不适用于本机的方案

- `0x751` + `AcpiTest_MULong.GetSetULong`：本机没有这个 WMI 类。
- `perfmode` 的 EC `0xE4`：针对无界14 Pro。
- 星耀14 AMD 版（P916F-STX）的资料：平台不同。

## 监控数据来源（初步摸底）

| 数据 | 来源 | 状态 |
|---|---|---|
| 核心频率 | `\Processor Information(0,*)\Actual Frequency` | 可读 |
| 核心分组 | 线程 0–3 P 核，4–11 E 核，12–15 LPE 核 | 由拓扑推断 |
| 封装功率 | `\Energy Meter(rapl_package0_pkg)\Power`（mW） | 可读 |
| GPU 功耗 | `\Energy Meter(rapl_package0_pp1)\Power`（mW） | 已验证随核显负载变化 |
| GPU 频率 | Intel `ControlLib.dll`，`ctlInit` 需带 Level Zero 标志，域类型 0 | 已验证，常驻内存增加约 43 MB |
| 封装温度 | `root\WMI` `EsifDeviceInformation.Temperature`，需管理员（实现见 open-revo 的 `esif_thermal.rs`） | 已验证：`InstanceName` 以 `_0` 结尾的实例，需管理员；ACPI 热区固定为 28°C，不可用 |
| 风扇转速 | PM2 端口，见上文 | 已验证 |

## 核显负载观察

平衡模式下只跑 OpenCL 核显负载 15 秒：封装约 41 W，`pp1` 约 30 W。`pp1` 随核显负载上升，可作为核显功耗来源。这高于只跑 CPU 时的持续 31.9 W，说明只跑 CPU 时限制它的不一定是封装功耗上限。这只是短时结果，持续表现待 `thermal-test.ps1 -Load both` 确认。
