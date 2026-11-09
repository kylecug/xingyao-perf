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

办公模式的功耗确实下降。性能与平衡基本相同，原因待查（`thermal-test.ps1`）。

待验证：

- 性能模式为什么没有比平衡更高，以及官方界面切换是否不同。
- 停用官方服务后的重启、睡眠唤醒和 Fn 键行为。

## 风扇（PM2 端口，未在本机实测）

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
| GPU 功耗 | `\Energy Meter(rapl_package0_pp1)\Power`（mW） | 可读，含义待核实 |
| GPU 频率 | Intel `ControlLib.dll` | 未测试 |
| 封装温度 | `root\WMI` `EsifDeviceInformation.Temperature`，需管理员（实现见 open-revo 的 `esif_thermal.rs`） | 待本机确认；ACPI 热区固定为 28°C，不可用 |
| 风扇转速 | PM2 端口，见上文 | 待本机确认 |

## 核显负载观察

平衡模式下只跑 OpenCL 核显负载 15 秒：封装约 41 W，`pp1` 约 30 W。`pp1` 随核显负载上升，可作为核显功耗来源。这高于只跑 CPU 时的持续 31.9 W，说明只跑 CPU 时限制它的不一定是封装功耗上限。这只是短时结果，持续表现待 `thermal-test.ps1 -Load both` 确认。
