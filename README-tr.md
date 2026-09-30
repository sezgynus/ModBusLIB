# ModBusLIB

[English](README.md) | [Türkçe](README-tr.md)

.NET Framework 4.8 hedefleyen C# uygulamaları için hafif bir Modbus RTU master kütüphanesi. ModBusLIB, RTU çerçevelerini oluşturup doğrular, `System.IO.Ports.SerialPort` üzerinden haberleşir, cevapları tiplenmiş olaylarla uygulamaya iletir ve harici NuGet bağımlılığı olmadan ayarlanabilir timeout/retry desteği sağlar.

## Özellikler

- Seri port üzerinden Modbus RTU master haberleşmesi
- `0x01`, `0x02`, `0x03`, `0x04`, `0x05`, `0x06`, `0x0F` ve `0x10` fonksiyon kodları
- CRC16/MODBUS üretimi ve doğrulaması
- 1–247 arası slave ID desteği
- Modbus `t3.5` sessizlik süresine göre RTU çerçeve sonu algılama
- Aynı anda tek aktif istek ve istek-cevap eşleştirmesi
- Ayarlanabilir timeout ve retry mekanizması
- Standart 5 baytlık Modbus exception cevabı desteği
- Coil/discrete-input okumalarında çözülmüş `bool[]` değerleri
- Register okumalarında çözülmüş `ushort[]` değerleri
- Protokol adet ve adres aralığı kontrolleri
- GitHub Actions üzerinde otomatik regression testleri

## Desteklenen fonksiyonlar

| Metot | Fonksiyon | İşlem | Maksimum adet |
| --- | --- | --- | ---: |
| `ReadCoils` | `0x01` | Coil oku | 2000 bit |
| `ReadDiscreteInputs` | `0x02` | Discrete Input oku | 2000 bit |
| `ReadHoldingRegisters` | `0x03` | Holding Register oku | 125 register |
| `ReadInputRegisters` | `0x04` | Input Register oku | 125 register |
| `WriteSingleCoil` | `0x05` | Tek coil yaz | 1 coil |
| `WriteSingleRegister` | `0x06` | Tek register yaz | 1 register |
| `WriteMultipleCoils` | `0x0F` | Birden fazla coil yaz | 1968 bit |
| `WriteMultipleRegisters` | `0x10` | Birden fazla register yaz | 123 register |

Adresler doğrudan 16 bit Modbus protokol adres alanına yazılır. `40001` gibi insan tarafından kullanılan referans gösterimleri otomatik olarak protokol adresine dönüştürülmez.

## Gereksinimler

- Windows
- .NET Framework 4.8
- `System.IO.Ports.SerialPort` tarafından desteklenen bir seri arayüz
- .NET Framework 4.8 targeting pack içeren Visual Studio veya eşdeğer MSBuild ortamı

Kütüphanenin harici NuGet bağımlılığı yoktur.

## Derleme

`ModBusLIB.sln` dosyasını Visual Studio ile açıp `Debug | Any CPU` veya `Release | Any CPU` yapılandırmasıyla derleyin.

Oluşan DLL:

```text
ModBusLIB/bin/Debug/ModBusLIB.dll
```

veya:

```text
ModBusLIB/bin/Release/ModBusLIB.dll
```

altında bulunur. DLL'i .NET Framework uygulamanıza referans olarak ekleyebilirsiniz.

## Seri port ayarları

İstek göndermeden önce kütüphaneyi başlatın:

```csharp
var bus = new ModBus();
bus.Initialize("COM3", 115200, Parity.Even);
```

Seri format 8 data bittir. Even veya Odd parity kullanıldığında bir stop biti, `Parity.None` kullanıldığında iki stop biti seçilir.

19200 baud üzerindeki hızlarda RTU zamanlaması `t1.5` için 750 µs ve `t3.5` için 1750 µs sabit değerlerini kullanır. Daha düşük baud hızlarında süreler baud rate üzerinden hesaplanır.

Haberleşme tamamlandığında `Close()` çağrılmalıdır.

## Temel okuma örneği

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

## Register yazma

`0x06` ile tek register yazma:

```csharp
bus.WriteSingleRegister(1, 10, 1234);
```

`0x10` ile birden fazla register yazma:

```csharp
ushort[] values = { 100, 200, 300 };
bus.WriteMultipleRegisters(1, 10, (ushort)values.Length, values);
```

Aynı anda yalnızca bir istek aktif olabilir. Mevcut istek cevaplanmadan veya timeout olmadan ikinci bir istek gönderilirse `InvalidOperationException` oluşur.

## Coil yazma

Tek coil Boolean değer ile yazılır:

```csharp
bus.WriteSingleCoil(1, 5, true);
```

`WriteMultipleCoils` için coil değerleri Modbus bit sırasına göre paketlenmiş byte dizisi olarak verilir:

```csharp
byte[] packedCoils = { 0b00000101 }; // coil 0 ve 2 ON
bus.WriteMultipleCoils(1, 0, 8, packedCoils);
```

## Cevap olayları

Desteklenen her fonksiyon kodunun karşılık gelen bir cevap olayı vardır:

| Olay | Fonksiyon |
| --- | --- |
| `ReadCoilsResponseHandler` | `0x01` |
| `ReadDiscreteInputsResponseHandler` | `0x02` |
| `ReadHoldingRegistersResponseHandler` | `0x03` |
| `ReadInputRegistersResponseHandler` | `0x04` |
| `WriteSingleCoilResponseHandler` | `0x05` |
| `WriteSingleRegisterResponseHandler` | `0x06` |
| `WriteMultipleCoilsResponseHandler` | `0x0F` |
| `WriteMultipleRegistersResponseHandler` | `0x10` |

`ReadResponseArgs` içeriği:

| Özellik | Açıklama |
| --- | --- |
| `Frame` | Slave adresi ve CRC dahil alınan tam RTU çerçevesi |
| `CrcOk` | Uygulamaya iletilen cevabın CRC doğrulama sonucu |
| `SlaveId` | Cevaptaki slave adresi |
| `IsException` | Modbus exception cevabında `true` |
| `ExceptionCode` | Modbus exception kodu |
| `Bits` | FC01/FC02 cevapları için çözülmüş bit değerleri |
| `Registers` | FC03/FC04 cevapları için çözülmüş 16 bit register değerleri |

CRC'si hatalı olan veya aktif istekle eşleşmeyen çerçeveler normal cevap olayı olarak uygulamaya iletilmeden atılır.

## Timeout ve retry

Varsayılan timeout 1000 ms'dir ve retry varsayılan olarak kapalıdır:

```csharp
bus.ResponseTimeoutMs = 1000;
bus.MaxRetries = 0;
```

Bir isteği en fazla iki kez yeniden göndermek için:

```csharp
bus.MaxRetries = 2;
```

Tüm denemeler timeout olduğunda `RequestTimeoutHandler`, slave ID, fonksiyon kodu ve retry sayısı ile tetiklenir. Timeout tamamlandıktan sonra yeni istek için request slot'u serbest bırakılır.

## Girdi doğrulama

Public istek metotları serileştirmeden önce geçersiz Modbus parametrelerini reddeder. Bunlara şunlar dahildir:

- 1–247 dışında slave ID
- sıfır veya protokol sınırını aşan adet
- 16 bit Modbus adres alanını aşan adres aralığı
- istenen coil adedi için yetersiz paketlenmiş coil verisi
- istenen register adedi için yetersiz register verisi

Uygulama, Modbus RTU'nun 256 bayt ADU sınırı içinde tam boy FC0F ve FC10 isteklerini destekler; bu fonksiyonların ürettiği maksimum request uzunluğu 255 bayttır.

## Eksiksiz API kullanımı


Bu bölüm `ModBus` sınıfının dışarı açtığı tüm public üyeleri kapsar.

<details>
<summary><strong>Nesne durumu ve ayarlar</strong></summary>


```csharp
var bus = new ModBus();

bus.ResponseTimeoutMs = 1000;
bus.MaxRetries = 1;

bus.Initialize("COM3", 115200, Parity.Even);

SerialPort serialPort = bus.Port; // Aktif SerialPort'a salt okunur erişim.
int crcErrors = bus.CrcFailCount;
```

| Üye | Tip | Amaç |
| --- | --- | --- |
| `Port` | `SerialPort` | Aktif seri port nesnesi. Setter private'tır. |
| `CrcFailCount` | `int` | CRC hatası nedeniyle reddedilen alınmış frame sayısı. |
| `ResponseTimeoutMs` | `int` | Bir request denemesinin retry/timeout işleminden önce bekleyeceği süre. Varsayılan: 1000 ms. |
| `MaxRetries` | `int` | İlk denemeden sonra yapılacak yeniden gönderim sayısı. Varsayılan: 0. |

</details>

<details>
<summary><strong>Initialize</strong></summary>


```csharp
bus.Initialize("COM3");                         // 115200, Even parity
bus.Initialize("COM3", 9600, Parity.Even);
bus.Initialize("COM3", 19200, Parity.None);
```

İmza:

```csharp
void Initialize(string portName, int baudRate = 115200, Parity parity = Parity.Even)
```

Port bu çağrı sırasında açılır. 8 data bit kullanılır; Even/Odd parity ile bir stop biti, `Parity.None` ile iki stop biti kullanılır.

</details>

<details>
<summary><strong>Close</strong></summary>


```csharp
bus.Close();
```

RX worker'ını kontrollü biçimde durdurur, RX durumunu sıfırlar, dahili timer'ı durdurur ve seri portu kapatır.

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

    // bits[0], istenen ilk coil'e karşılık gelir.
    for (int i = 0; i < e.Bits.Length; i++)
        Console.WriteLine($"Bit {i}: {e.Bits[i]}");
};

bus.ReadCoils(1, 0, 8);
```

İmza: `void ReadCoils(byte slave_id, ushort start, ushort count)`. Adet: 1–2000 coil. Coil verisi LSB-first olarak `ReadResponseArgs.bits` dizisine çözülür. İstenen adet 8'in katı değilse son byte padding bitleri içerir; bu bitler önemliyse iterasyonda request'teki `count` değerini sınır olarak kullanın.

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

İmza: `void ReadDiscreteInputs(byte slave_id, ushort start, ushort count)`. Adet: 1–2000 input. Çözülmüş değerler `Bits` içinde döner.

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

İmza: `void ReadHoldingRegisters(byte slave_id, ushort start, ushort count)`. Adet: 1–125 register. Register byte'ları big-endian olarak `ushort[] registers` dizisine çözülür.

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

İmza: `void ReadInputRegisters(byte slave_id, ushort start, ushort count)`. Adet: 1–125 register. Çözülmüş değerler `Registers` içinde döner.

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

İmza: `void WriteSingleCoil(byte slave_id, ushort adress, bool coil_value)`. `true`, `FF 00`; `false`, `00 00` olarak serileştirilir. Normal FC05 cevabı request alanlarını echo eder ve aktif request ile karşılaştırılarak doğrulanır.

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

İmza: `int WriteSingleRegister(byte slave_id, ushort adress, ushort udata)`. Normal FC06 cevabı request'in echo'su olarak doğrulanır. Dönüş değeri serileştirilmiş request frame uzunluğudur.

</details>

<details>
<summary><strong>FC0F — WriteMultipleCoils</strong></summary>


```csharp
bus.WriteMultipleCoilsResponseHandler += (sender, e) =>
{
    if (!e.IsException)
        Console.WriteLine("Multiple-coil write acknowledged.");
};

// LSB-first: bit 0 ve bit 2 ON.
byte[] coilData = { 0b00000101 };
int frameLength = bus.WriteMultipleCoils(1, 0, 8, coilData);
```

İmza: `int WriteMultipleCoils(byte slave_id, ushort start, ushort count, byte[] pdata)`. Adet: 1–1968 coil. `pdata` en az `ceil(count / 8)` byte içermelidir. Cevaptaki başlangıç adresi ve adet request ile karşılaştırılır. Metot serileştirilmiş request frame uzunluğunu döndürür.

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

İmza: `int WriteMultipleRegisters(byte slave_id, ushort start, ushort count, ushort[] udata)`. Adet: 1–123 register. `udata` en az `count` eleman içermelidir. Değerler high byte önce olacak şekilde serileştirilir. Cevaptaki başlangıç adresi ve adet request ile karşılaştırılır. Metot serileştirilmiş request frame uzunluğunu döndürür.

</details>

<details>
<summary><strong>ReadResponseArgs</strong></summary>


Sekiz fonksiyona ait response event'lerinin tamamı `ReadResponseArgs` kullanır:

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

Tarihsel isminden farklı olarak `Frame`, slave adresi ve CRC dahil alınan tam RTU frame'ini içerir. CRC'si geçersiz frame'ler event oluşturulmadan atıldığı için uygulamaya iletilen normal cevaplarda `crc_ok == true` olur. `Bits` yalnız başarılı FC01/FC02 okumalarında, `Registers` ise yalnız başarılı FC03/FC04 okumalarında doldurulur.

</details>

<details>
<summary><strong>Modbus exception cevapları</strong></summary>


Modbus exception cevabı, orijinal fonksiyonun event'i üzerinden iletilir:

```csharp
bus.ReadHoldingRegistersResponseHandler += (sender, e) =>
{
    if (e.IsException)
    {
        Console.WriteLine($"Slave {e.SlaveId} exception döndürdü: 0x{e.ExceptionCode:X2}");
        return;
    }

    // e.Registers burada işlenebilir.
};
```

Kütüphane standart 5 baytlık RTU exception frame'lerini kabul eder ve exception kodunu `ExceptionCode` üzerinden verir.

</details>

<details>
<summary><strong>RequestTimeoutHandler ve RequestTimeoutArgs</strong></summary>


```csharp
bus.ResponseTimeoutMs = 500;
bus.MaxRetries = 2;

bus.RequestTimeoutHandler += (sender, e) =>
{
    Console.WriteLine($"Slave: {e.SlaveId}");
    Console.WriteLine($"Function: 0x{e.Function:X2}");
    Console.WriteLine($"Yapılan retry: {e.Retries}");
};
```

`RequestTimeoutArgs`; `SlaveId`, `Function` ve `Retries` alanlarını sunar. Event yalnız ilk deneme ve ayarlanan tüm retry'lar timeout olduktan sonra tetiklenir.

</details>

<details>
<summary><strong>Aynı anda tek aktif request kuralı</strong></summary>


Tüm public Modbus request metotları aynı request-tracking mekanizmasını kullanır:

```csharp
bus.ReadHoldingRegisters(1, 0, 1);

// Response event'i veya nihai timeout gelmeden yeni request göndermeyin.
// İlk request hâlâ aktifken aşağıdaki çağrı InvalidOperationException üretir:
// bus.ReadInputRegisters(1, 0, 1);
```

Bir cevap yalnız aktif request'in slave ID ve fonksiyon koduyla eşleştiğinde kabul edilir. Read cevaplarında beklenen byte count da kontrol edilir; write cevaplarında ise beklenen echo/adres/adet alanları doğrulanır.

</details>

<details>
<summary><strong>Tam yaşam döngüsü örneği</strong></summary>


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
    Console.WriteLine($"Slave {e.SlaveId} cevap vermedi");
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

## Thread yapısı

Seri port alımı ve RTU çerçeve tamamlama işlemleri arka plan thread'lerinde yürütülür. Ortak RX durumu kütüphane içinde senkronize edilir.

Bu nedenle response ve timeout event handler'larının UI thread üzerinde çalışacağı garanti edilmez. Windows Forms veya WPF uygulamalarında UI güncellemeleri uygun dispatch yöntemiyle UI thread'e aktarılmalıdır.

`Close()`, worker thread'i kontrollü biçimde durdurur ve seri portu kapatır.

## Testler

`ModBusLIB.Tests`, harici test framework bağımlılığı olmayan bir regression test executable'ıdır. Test paketi şunları kapsar:

- bilinen CRC16/MODBUS test vektörü
- request serialization
- maksimum FC0F ve FC10 frame boyutları
- slave ve fonksiyon eşleştirmesi
- read byte-count doğrulaması
- standart 5 bayt exception cevapları
- write-response echo doğrulaması
- register ve coil çözümleme
- Modbus adet ve adres sınırları

GitHub Actions, Windows üzerinde .NET Framework 4.8 kütüphanesini ve test executable'ını derler ve `master` branch'ine yapılan push'larda regression testlerini çalıştırır.

Otomatik test paketi fiziksel Modbus donanımı gerektirmeden protokol mantığını doğrular. Seri port sürücüsü davranışı, RS-485 direction control, elektriksel katman ve cihaza özgü zamanlama davranışları gerçek donanım üzerinde ayrıca test edilmelidir.

## Proje yapısı

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
    └── sign-commits.yml
```

## İlgili proje

[TivaC-MODBUS](https://github.com/sezgynus/TivaC-MODBUS), Modbus RTU haberleşmesi için ModBusLIB kullanan bir Windows Forms uygulamasıdır.
