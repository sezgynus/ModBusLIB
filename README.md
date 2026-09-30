# ModBusLIB

[English](README.md) | [Türkçe](README-tr.md)

A lightweight Modbus RTU master library for C# applications targeting .NET Framework 4.8. ModBusLIB builds and validates RTU frames, communicates through `System.IO.Ports.SerialPort`, dispatches typed response events, and provides configurable request timeouts and retries without external NuGet dependencies.

## Features

- Modbus RTU master communication over a serial port
- Function codes `0x01`, `0x02`, `0x03`, `0x04`, `0x05`, `0x06`, `0x0F`, and `0x10`
- CRC16/MODBUS generation and validation
- Slave IDs from 1 to 247
- RTU frame boundary detection using the Modbus `t3.5` silent interval
- One outstanding request at a time with response matching
- Configurable timeout and retry handling
- Standard 5-byte Modbus exception response support
- Decoded `bool[]` values for coil/discrete-input reads
- Decoded `ushort[]` values for register reads
- Protocol quantity and address-range validation
- Automated regression tests on GitHub Actions

## Supported function codes

| Method | Function | Operation | Maximum quantity |
| --- | --- | --- | ---: |
| `ReadCoils` | `0x01` | Read Coils | 2000 bits |
| `ReadDiscreteInputs` | `0x02` | Read Discrete Inputs | 2000 bits |
| `ReadHoldingRegisters` | `0x03` | Read Holding Registers | 125 registers |
| `ReadInputRegisters` | `0x04` | Read Input Registers | 125 registers |
| `WriteSingleCoil` | `0x05` | Write Single Coil | 1 coil |
| `WriteSingleRegister` | `0x06` | Write Single Register | 1 register |
| `WriteMultipleCoils` | `0x0F` | Write Multiple Coils | 1968 bits |
| `WriteMultipleRegisters` | `0x10` | Write Multiple Registers | 123 registers |

Addresses are written directly to the 16-bit Modbus protocol address field. Human-readable reference notation such as `40001` is not converted automatically.

## Requirements

- Windows
- .NET Framework 4.8
- A serial interface supported by `System.IO.Ports.SerialPort`
- Visual Studio with the .NET Framework 4.8 targeting pack, or an equivalent MSBuild environment

The library has no external NuGet dependencies.

## Build

Open `ModBusLIB.sln` in Visual Studio and build the solution using `Debug | Any CPU` or `Release | Any CPU`.

The resulting assembly is generated under:

```text
ModBusLIB/bin/Debug/ModBusLIB.dll
```

or:

```text
ModBusLIB/bin/Release/ModBusLIB.dll
```

Add the DLL as a reference to your .NET Framework application.

## Serial configuration

Initialize the library before sending requests:

```csharp
var bus = new ModBus();
bus.Initialize("COM3", 115200, Parity.Even);
```

The serial format uses 8 data bits. Even or odd parity uses one stop bit. `Parity.None` uses two stop bits.

For baud rates above 19200, RTU timing uses fixed values of 750 µs for `t1.5` and 1750 µs for `t3.5`. At lower baud rates the timing values are calculated from the baud rate.

Call `Close()` when communication is finished.

## Basic read example

```csharp
using System;
using System.IO.Ports;
using ModBusLIB;

class Program
{
    static void Main()
    {
        var bus = new ModBus
        {
            ResponseTimeoutMs = 1000,
            MaxRetries = 1
        };

        bus.ReadHoldingRegistersResponseHandler += (sender, e) =>
        {
            if (e.IsException)
            {
                Console.WriteLine($"Modbus exception: 0x{e.ExceptionCode:X2}");
                return;
            }

            foreach (ushort value in e.Registers)
                Console.WriteLine(value);
        };

        bus.RequestTimeoutHandler += (sender, e) =>
        {
            Console.WriteLine(
                $"Timeout: slave={e.SlaveId}, function=0x{e.Function:X2}, retries={e.Retries}");
        };

        bus.Initialize("COM3", 115200, Parity.Even);

        try
        {
            bus.ReadHoldingRegisters(1, 0, 2);
            Console.ReadLine();
        }
        finally
        {
            bus.Close();
        }
    }
}
```

## Writing registers

Write one register with function `0x06`:

```csharp
bus.WriteSingleRegister(1, 10, 1234);
```

Write multiple registers with function `0x10`:

```csharp
ushort[] values = { 100, 200, 300 };
bus.WriteMultipleRegisters(1, 10, (ushort)values.Length, values);
```

Only one request may be outstanding at a time. Sending another request before the current request completes or times out throws `InvalidOperationException`.

## Writing coils

A single coil is written with a Boolean value:

```csharp
bus.WriteSingleCoil(1, 5, true);
```

For `WriteMultipleCoils`, coil values are supplied as packed bytes in Modbus bit order:

```csharp
byte[] packedCoils = { 0b00000101 }; // coils 0 and 2 are ON
bus.WriteMultipleCoils(1, 0, 8, packedCoils);
```

## Response events

Each supported function code has a corresponding response event:

| Event | Function |
| --- | --- |
| `ReadCoilsResponseHandler` | `0x01` |
| `ReadDiscreteInputsResponseHandler` | `0x02` |
| `ReadHoldingRegistersResponseHandler` | `0x03` |
| `ReadInputRegistersResponseHandler` | `0x04` |
| `WriteSingleCoilResponseHandler` | `0x05` |
| `WriteSingleRegisterResponseHandler` | `0x06` |
| `WriteMultipleCoilsResponseHandler` | `0x0F` |
| `WriteMultipleRegistersResponseHandler` | `0x10` |

`ReadResponseArgs` contains:

| Property | Description |
| --- | --- |
| `Frame` | Complete received RTU frame, including slave address and CRC |
| `CrcOk` | CRC validation result for a dispatched response |
| `SlaveId` | Slave address from the response |
| `Function` | Original Modbus function code |
| `StartAddress` | Start address captured from the matching request |
| `RequestedQuantity` | Requested bit/register quantity; 1 for FC05/FC06 |
| `IsException` | `true` for a Modbus exception response |
| `ExceptionCode` | Modbus exception code |
| `Bits` | Decoded bit values for FC01/FC02 responses |
| `Registers` | Decoded 16-bit values for FC03/FC04 responses |

Frames with an invalid CRC or frames that do not match the outstanding request are discarded rather than dispatched as normal responses.

## Timeout and retry handling

The default timeout is 1000 ms and retries are disabled by default:

```csharp
bus.ResponseTimeoutMs = 1000;
bus.MaxRetries = 0;
```

To retry a request up to two times:

```csharp
bus.MaxRetries = 2;
```

If all attempts expire, `RequestTimeoutHandler` is raised with the slave ID, function code, and retry count. Once the timeout is completed, the request slot becomes available for the next request.

## Validation

The public request methods reject invalid Modbus parameters before serialization, including:

- slave IDs outside 1–247
- zero or excessive quantities
- address ranges extending beyond the 16-bit Modbus address space
- insufficient packed coil data
- insufficient register data

The implementation supports full-size FC0F and FC10 request frames up to the Modbus RTU 256-byte ADU limit; the maximum requests generated by these functions are 255 bytes.

## Complete API usage


This section covers every public member exposed by `ModBus`.

<details>
<summary><strong>Object state and configuration</strong></summary>


```csharp
var bus = new ModBus();

// Configurable before requests are sent.
bus.ResponseTimeoutMs = 1000;
bus.MaxRetries = 1;

bus.Initialize("COM3", 115200, Parity.Even);

SerialPort serialPort = bus.Port; // Read-only reference to the active SerialPort.
int crcErrors = bus.CrcFailCount;
```

| Member | Type | Purpose |
| --- | --- | --- |
| `IsOpen` | `bool` | Whether the internal serial transport is open. |
| `PortName` | `string` | Active serial port name, or `null` when closed. |
| `CrcFailCount` | `int` | Number of received frames rejected because their CRC was invalid. |
| `ResponseTimeoutMs` | `int` | Time allowed for a request attempt before retry/timeout processing. Default: 1000 ms. |
| `MaxRetries` | `int` | Number of retransmissions after the initial attempt. Default: 0. |

</details>

<details>
<summary><strong>Initialize</strong></summary>


```csharp
bus.Initialize("COM3");                         // 115200, Even parity
bus.Initialize("COM3", 9600, Parity.Even);
bus.Initialize("COM3", 19200, Parity.None);
```

Signature:

```csharp
void Initialize(string portName, int baudRate = 115200, Parity parity = Parity.Even)
```

The library opens the port during this call. The underlying `SerialPort` is intentionally kept internal; use `IsOpen` and `PortName` to inspect connection state. It uses 8 data bits; Even/Odd parity uses one stop bit and `Parity.None` uses two stop bits.

</details>

<details>
<summary><strong>Close</strong></summary>


```csharp
bus.Close();
```

Stops the receive worker cooperatively, resets receive state, stops the internal timer, and closes the serial port.

</details>

<details>
<summary><strong>FC01 — ReadCoils</strong></summary>


```csharp
bus.ReadCoilsResponseHandler += (sender, e) =>
{
    if (e.IsException)
    {
        Console.WriteLine($"Exception 0x{e.ExceptionCode:X2}");
        return;
    }

    // bits[0] corresponds to the first requested coil.
    for (int i = 0; i < e.Bits.Length; i++)
        Console.WriteLine($"Bit {i}: {e.Bits[i]}");
};

bus.ReadCoils(1, 0, 8);
```

Signature: `void ReadCoils(byte slaveId, ushort start, ushort count)`. Quantity: 1–2000 coils. Coil data is decoded LSB-first into `ReadResponseArgs.Bits`. `Bits.Length` exactly matches the requested quantity; padding bits in the final RTU data byte are not exposed.

</details>

<details>
<summary><strong>FC02 — ReadDiscreteInputs</strong></summary>


```csharp
bus.ReadDiscreteInputsResponseHandler += (sender, e) =>
{
    if (!e.IsException)
    {
        bool input0 = e.Bits[0];
        Console.WriteLine($"Input 0: {input0}");
    }
};

bus.ReadDiscreteInputs(1, 0, 8);
```

Signature: `void ReadDiscreteInputs(byte slaveId, ushort start, ushort count)`. Quantity: 1–2000 inputs. Decoded values are returned in `Bits`.

</details>

<details>
<summary><strong>FC03 — ReadHoldingRegisters</strong></summary>


```csharp
bus.ReadHoldingRegistersResponseHandler += (sender, e) =>
{
    if (!e.IsException)
    {
        for (int i = 0; i < e.Registers.Length; i++)
            Console.WriteLine($"Holding register {i}: {e.Registers[i]}");
    }
};

bus.ReadHoldingRegisters(1, 0, 2);
```

Signature: `void ReadHoldingRegisters(byte slaveId, ushort start, ushort count)`. Quantity: 1–125 registers. Register bytes are decoded big-endian into `ushort[] Registers`.

</details>

<details>
<summary><strong>FC04 — ReadInputRegisters</strong></summary>


```csharp
bus.ReadInputRegistersResponseHandler += (sender, e) =>
{
    if (!e.IsException)
        Console.WriteLine($"Input register: {e.Registers[0]}");
};

bus.ReadInputRegisters(1, 0, 1);
```

Signature: `void ReadInputRegisters(byte slaveId, ushort start, ushort count)`. Quantity: 1–125 registers. Decoded values are returned in `Registers`.

</details>

<details>
<summary><strong>FC05 — WriteSingleCoil</strong></summary>


```csharp
bus.WriteSingleCoilResponseHandler += (sender, e) =>
{
    if (e.IsException)
        Console.WriteLine($"Write failed: 0x{e.ExceptionCode:X2}");
    else
        Console.WriteLine("Coil write acknowledged.");
};

bus.WriteSingleCoil(1, 5, true);
```

Signature: `void WriteSingleCoil(byte slaveId, ushort address, bool coilValue)`. `true` is serialized as `FF 00`; `false` as `00 00`. A normal FC05 response must echo the request fields and is validated against the outstanding request.

</details>

<details>
<summary><strong>FC06 — WriteSingleRegister</strong></summary>


```csharp
bus.WriteSingleRegisterResponseHandler += (sender, e) =>
{
    if (!e.IsException)
        Console.WriteLine("Register write acknowledged.");
};

int frameLength = bus.WriteSingleRegister(1, 10, 1234);
Console.WriteLine($"TX frame length: {frameLength}");
```

Signature: `int WriteSingleRegister(byte slaveId, ushort address, ushort value)`. A normal FC06 response is validated as an echo of the request. The return value is the serialized request-frame length.

</details>

<details>
<summary><strong>FC0F — WriteMultipleCoils</strong></summary>


```csharp
bus.WriteMultipleCoilsResponseHandler += (sender, e) =>
{
    if (!e.IsException)
        Console.WriteLine("Multiple-coil write acknowledged.");
};

// LSB-first: bit 0 and bit 2 are ON.
byte[] coilData = { 0b00000101 };
int frameLength = bus.WriteMultipleCoils(1, 0, 8, coilData);
```

Signature: `int WriteMultipleCoils(byte slaveId, ushort start, ushort count, byte[] data)`. Quantity: 1–1968 coils. `pdata` must contain at least `ceil(count / 8)` bytes. The response's start address and quantity are checked against the request. The method returns the serialized request-frame length.

</details>

<details>
<summary><strong>FC10 — WriteMultipleRegisters</strong></summary>


```csharp
bus.WriteMultipleRegistersResponseHandler += (sender, e) =>
{
    if (!e.IsException)
        Console.WriteLine("Multiple-register write acknowledged.");
};

ushort[] values = { 100, 200, 300 };
int frameLength = bus.WriteMultipleRegisters(
    1, 10, (ushort)values.Length, values);
```

Signature: `int WriteMultipleRegisters(byte slaveId, ushort start, ushort count, ushort[] data)`. Quantity: 1–123 registers. `udata` must contain at least `count` elements. Values are serialized high byte first. The response's start address and quantity are checked against the request. The method returns the serialized request-frame length.

</details>

<details>
<summary><strong>ReadResponseArgs</strong></summary>


All eight function-specific response events use `ReadResponseArgs`:

```csharp
bus.ReadHoldingRegistersResponseHandler += (sender, e) =>
{
    Console.WriteLine($"Slave: {e.SlaveId}");
    Console.WriteLine($"CRC valid: {e.CrcOk}");

    if (e.IsException)
    {
        Console.WriteLine($"Exception code: 0x{e.ExceptionCode:X2}");
        return;
    }

    byte[] rawFrame = e.Frame;
    ushort[] registers = e.Registers; // FC03 / FC04
    bool[] bits = e.Bits;             // FC01 / FC02
};
```

Despite its historical name, `Frame` contains the complete received RTU frame, including slave address and CRC. Each `Frame` access returns a defensive copy, so consumer mutations cannot alter the response snapshot held by the library. `Function`, `StartAddress`, and `RequestedQuantity` correlate the response with the completed request. Invalid-CRC frames are discarded before an event is raised, so normally dispatched responses have `crc_ok == true`. `Bits` is populated only for successful FC01/FC02 reads, and `Registers` only for successful FC03/FC04 reads.

</details>

<details>
<summary><strong>Modbus exception responses</strong></summary>


A Modbus exception response is delivered through the event belonging to the original function:

```csharp
bus.ReadHoldingRegistersResponseHandler += (sender, e) =>
{
    if (e.IsException)
    {
        Console.WriteLine($"Slave {e.SlaveId} returned exception 0x{e.ExceptionCode:X2}");
        return;
    }

    // Process e.Registers here.
};
```

The library accepts standard 5-byte RTU exception frames and exposes the exception code through `ExceptionCode`.

</details>

<details>
<summary><strong>RequestTimeoutHandler and RequestTimeoutArgs</strong></summary>


```csharp
bus.ResponseTimeoutMs = 500;
bus.MaxRetries = 2;

bus.RequestTimeoutHandler += (sender, e) =>
{
    Console.WriteLine($"Slave: {e.SlaveId}");
    Console.WriteLine($"Function: 0x{e.Function:X2}");
    Console.WriteLine($"Retries performed: {e.Retries}");
};
```

`RequestTimeoutArgs` exposes `SlaveId`, `Function`, and `Retries`. The event is raised only after the initial attempt and all configured retries have expired.

</details>

<details>
<summary><strong>CallbackExceptionHandler and CallbackExceptionArgs</strong></summary>

Response and timeout callbacks are isolated from the Modbus worker. If application callback code throws, the worker continues running and the failure is reported through `CallbackExceptionHandler`:

```csharp
bus.CallbackExceptionHandler += (sender, e) =>
{
    Console.WriteLine($"Callback {e.CallbackName} failed: {e.Exception}");
};
```

`CallbackExceptionArgs` exposes `CallbackName` and `Exception`. Exceptions thrown by a diagnostic callback are also contained so they cannot terminate the worker thread.

</details>

<details>
<summary><strong>One outstanding request rule</strong></summary>


Every public Modbus request method participates in the same request-tracking mechanism:

```csharp
bus.ReadHoldingRegisters(1, 0, 1);

// Do not send another request until the response event or final timeout.
// This throws InvalidOperationException while the first request is pending:
// bus.ReadInputRegisters(1, 0, 1);
```

A response is accepted only when it matches the active request's slave ID and function. Read responses are additionally checked for the expected byte count, while write responses are checked against the expected echo/address/quantity fields.

</details>

<details>
<summary><strong>Complete lifecycle example</strong></summary>


```csharp
var bus = new ModBus
{
    ResponseTimeoutMs = 1000,
    MaxRetries = 1
};

bus.ReadHoldingRegistersResponseHandler += (sender, e) =>
{
    if (e.IsException)
        Console.WriteLine($"Exception: 0x{e.ExceptionCode:X2}");
    else
        Console.WriteLine($"Value: {e.Registers[0]}");
};

bus.RequestTimeoutHandler += (sender, e) =>
{
    Console.WriteLine($"No response from slave {e.SlaveId}");
};

bus.Initialize("COM3", 9600, Parity.Even);

try
{
    bus.ReadHoldingRegisters(1, 0, 1);
    Console.ReadLine();
}
finally
{
    bus.Close();
}
```


</details>

## Threading

Serial receive processing and RTU frame completion run on background threads. Shared receive state is synchronized internally.

Response and timeout event handlers are therefore not guaranteed to execute on a UI thread. Windows Forms or WPF applications should marshal UI updates to their UI thread.

`Close()` stops the worker thread cooperatively and closes the serial port.

## Tests

`ModBusLIB.Tests` is a dependency-free regression test executable. The test suite covers:

- known CRC16/MODBUS vectors
- request serialization
- maximum FC0F and FC10 frame sizes
- slave and function matching
- read byte-count validation
- standard 5-byte exception responses
- write-response echo validation
- register and coil decoding
- Modbus quantity and address limits
- transport initialization/close/dispose failure cleanup

GitHub Actions builds the .NET Framework 4.8 library and test executable on Windows and runs the regression suite on pushes to `master`.

The automated suite validates protocol logic without requiring physical Modbus hardware. Serial-driver behavior, RS-485 direction control, electrical-layer behavior, and device-specific timing still require hardware testing.

## Project structure

```text
ModBusLIB.sln
├── ModBusLIB/
│   ├── ModBus.cs
│   ├── ModBusLIB.csproj
│   └── Properties/
├── ModBusLIB.Tests/
│   ├── ModBusLIB.Tests.csproj
│   └── Program.cs
└── .github/workflows/
    ├── modbus-tests.yml
    ├── release.yml
    └── sign-commits.yml
```

## Related project

[TivaC-MODBUS](https://github.com/sezgynus/TivaC-MODBUS) is a Windows Forms application that uses ModBusLIB for Modbus RTU communication.
