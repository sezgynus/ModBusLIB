# ModBusLIB

.NET Framework 4.8 için C# seri MODBUS RTU kütüphanesi. Okuma/yazma isteklerini oluşturur, CRC16/MODBUS ekler ve gelen cevapları fonksiyon koduna göre olaylarla uygulamaya iletir. [TivaC-MODBUS](https://github.com/sezgynus/TivaC-MODBUS) Windows Forms uygulaması bu kütüphaneyi kullanır.

## Desteklenen istekler

| Metot | Fonksiyon kodu | Parametreler |
|---|---|---|
| `ReadCoils` | `0x01` | slave, başlangıç, adet |
| `ReadDiscreteInputs` | `0x02` | slave, başlangıç, adet |
| `ReadHoldingRegisters` | `0x03` | slave, başlangıç, adet |
| `ReadInputRegisters` | `0x04` | slave, başlangıç, adet |
| `WriteSingleCoil` | `0x05` | slave, adres, bool |
| `WriteSingleRegister` | `0x06` | slave, adres, ushort |
| `WriteMultipleCoils` | `0x0F` | slave, başlangıç, bit adedi, paketlenmiş byte dizisi |
| `WriteMultipleRegisters` | `0x10` | slave, başlangıç, register adedi, ushort dizisi |

Adresler doğrudan 16 bit protokol alanına yazılır; `40001` gibi gösterim adresleri için otomatik taban dönüşümü yapılmaz. Register değerleri yüksek bayt önce, CRC düşük bayt önce serileştirilir.

## Derleme

1. Visual Studio’da .NET masaüstü geliştirme araçlarını ve .NET Framework **4.8** targeting pack’i kurun.
2. `ModBusLIB.sln` çözümünü açın.
3. `Debug | Any CPU` veya `Release | Any CPU` ile derleyin.
4. Oluşan `ModBusLIB/bin/Debug/ModBusLIB.dll` ya da Release karşılığını uygulamanıza referans olarak ekleyin.

Harici NuGet bağımlılığı yoktur; seri iletişim `System.IO.Ports.SerialPort` üzerinden sağlanır.

## Bağlantı ayarları

```csharp
Initialize(string portName, int baudRate = 115200, Parity parity = Parity.Even)
```

Veri uzunluğu 8 bittir. Even/Odd parity ile bir stop biti; `Parity.None` seçilirse iki stop biti kullanılır. Önce `Initialize()` çağrılmalı, port başarıyla açıldıktan sonra istek gönderilmelidir. İş bitiminde `Close()` çağrılır.

## Okuma örneği

Aşağıdaki örnek adresi `0x55` olan cihazdan bir holding register ister. COM portunu ve register adresini cihazınıza göre seçin.

```csharp
using System;
using System.IO.Ports;
using System.Threading;
using ModBusLIB;

class Program
{
    static void Main()
    {
        var bus = new ModBus();
        using (var received = new ManualResetEvent(false))
        {
            bus.ReadHoldingRegistersResponseHandler += (sender, e) =>
            {
                try
                {
                    if (!e.crc_ok || e.ex_resp || e.pdu.Length < 7)
                        return;

                    ushort value = (ushort)((e.pdu[3] << 8) | e.pdu[4]);
                    Console.WriteLine("Register 0: " + value);
                }
                finally
                {
                    received.Set();
                }
            };

            bus.Initialize("COM3", 115200, Parity.Even);
            try
            {
                bus.ReadHoldingRegisters(0x55, 0, 1);
                if (!received.WaitOne(2000))
                    Console.WriteLine("Cevap zaman aşımı");
            }
            finally
            {
                bus.Close();
            }
        }
    }
}
```

## Cevap olayları

Her işlem için aynı isimli `...ResponseHandler` olayı bulunur. `ReadResponseArgs` alanları:

| Alan | Anlam |
|---|---|
| `pdu` | İsminin aksine adres ve CRC dahil alınan tam RTU çerçevesi |
| `crc_ok` | Alınan CRC’nin hesaplanan CRC ile eşleşmesi |
| `slave_id` | Çerçevenin ilk baytı |
| `ex_resp` | Exception yanıtı işareti |
| `ex_code` | Exception kodu |

CRC başarısız olsa da olay çağrılabilir; veriyi kullanmadan `crc_ok` kontrol edilmelidir. Olaylar arka plan iş parçacığından gelir. Windows Forms/WPF arayüzünü güncellerken uygun UI dispatch yöntemini kullanın.

## Mevcut uygulama sınırları

- **Cevap adresi filtresi:** alım kodu yalnızca ilk baytı `0x55` olan çerçeveleri işler. Metotların farklı slave adresi kabul etmesi, bu adreslerin cevaplarının işleneceği anlamına gelmez.
- **Kısa cevaplar:** alım ayrıştırıcısı yalnızca 5 bayttan uzun paketleri işler. Standart 5 bayt exception cevapları ve 6 baytlık küçük coil cevapları bu koşulu geçmez.
- **Yazma tamponu:** TX tamponu 16 bayttır. Mevcut boyutla çoklu coil yazma en fazla 7 veri baytı/**56 bit**, çoklu register yazma en fazla **3 register** sığdırır. Girdi uzunluğu denetimi yapılmaz.
- **İstek sıralaması:** ortak TX/RX tamponları kullanılır; istek kuyruğu, otomatik retry ve istek-cevap eşleştirme katmanı yoktur. Aynı anda tek sorgu yönetin; zaman aşımını uygulama tarafında uygulayın.
- **Zamanlama:** `t1_5`/`t3_5` hesaplanır ancak paket bitirme yolu yaklaşık 250 µs yazılım tick’i ve 20’den büyük sayaç eşiği kullanır. Bu, işletim sistemi zamanlamasına bağlı bir uygulamadır.
- **Yaşam döngüsü:** `Close()` iş parçacığını `Thread.Abort()` ile sonlandırır. Proje .NET Framework hedeflidir; modern .NET için aynı yaşam döngüsünü doğrudan varsaymayın.

`Coils`, `DiscreteInputs`, `InputRegisters` ve `HoldingRegisters` dizileri public olarak tanımlıdır ancak cevap ayrıştırıcısı bunları otomatik doldurmaz. Veriler olayın `pdu` alanından çözülür. `crc_fail_count`, kontrol edilen çerçevelerdeki CRC hatalarını sayar.

## Kaynak düzeni

- `ModBusLIB/ModBus.cs`: public API, serileştirme, CRC ve alım olayları.
- `ModBusLIB/ModBusLIB.csproj`: .NET Framework 4.8 kütüphane projesi.
- `ModBusLIB/Properties/AssemblyInfo.cs`: assembly bilgileri.
- `ModBusLIB.sln`: Visual Studio çözümü.
