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

待验证：

- 三档的实际功耗差异。
- 性能模式切换。
- 停用官方服务后的重启、睡眠唤醒和 Fn 键行为。

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
| 封装温度 | ESIF 或 MSR（需驱动） | 未解决；ACPI 热区固定为 28°C，不可用 |
