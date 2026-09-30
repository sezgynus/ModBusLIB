using System;
using System.Reflection;
using ModBusLIB;

namespace ModBusLIB.Tests
{
    internal static class Program
    {
        private static int passed;
        private static int failed;

        private static void Assert(bool condition, string name)
        {
            if (!condition)
            {
                failed++;
                Console.WriteLine("[FAIL] " + name);
                return;
            }
            passed++;
            Console.WriteLine("[PASS] " + name);
        }

        private static object Invoke(ModBus bus, string name, params object[] args)
        {
            return typeof(ModBus).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bus, args);
        }

        private static object InvokeStatic(string name, params object[] args)
        {
            return typeof(ModBus).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, args);
        }

        private static void Set(ModBus bus, string name, object value)
        {
            typeof(ModBus).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(bus, value);
        }

        private static T Get<T>(ModBus bus, string name)
        {
            return (T)typeof(ModBus).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bus);
        }

        private static ushort Crc(ModBus bus, byte[] data)
        {
            return (ushort)Invoke(bus, "CRC16_MODBUS", data, data.Length);
        }

        private static byte[] WithCrc(ModBus bus, params byte[] body)
        {
            ushort crc = Crc(bus, body);
            byte[] frame = new byte[body.Length + 2];
            Array.Copy(body, frame, body.Length);
            frame[frame.Length - 2] = (byte)(crc & 0xff);
            frame[frame.Length - 1] = (byte)(crc >> 8);
            return frame;
        }

        private static void TestCrcAndSerialization()
        {
            var bus = new ModBus();
            byte[] body = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
            Assert(Crc(bus, body) == 0xCDC5, "CRC16/MODBUS known vector");

            Invoke(bus, "modbus_read_serializer", (byte)0x03, (byte)0x01, (ushort)0, (ushort)10);
            byte[] tx = Get<byte[]>(bus, "tx_buf");
            byte[] expected = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD };
            Assert(tx.Length == expected.Length, "FC03 request length");
            Assert(BitConverter.ToString(tx) == BitConverter.ToString(expected), "FC03 request bytes");

            ushort[] regs = new ushort[123];
            int regLen = (int)Invoke(bus, "modbus_write_serializer", (byte)0x10, (byte)1, (ushort)0, (ushort)123, null, regs);
            Assert(regLen == 255 && Get<byte[]>(bus, "tx_buf").Length == 255, "FC10 maximum frame fits 255 bytes");

            byte[] coils = new byte[246];
            int coilLen = (int)Invoke(bus, "modbus_write_serializer", (byte)0x0F, (byte)1, (ushort)0, (ushort)1968, coils, null);
            Assert(coilLen == 255 && Get<byte[]>(bus, "tx_buf").Length == 255, "FC0F maximum frame fits 255 bytes");
        }

        private static void PreparePending(ModBus bus, byte slave, byte function, byte[] request)
        {
            Set(bus, "request_pending", true);
            Set(bus, "pending_slave_id", slave);
            Set(bus, "pending_function", function);
            Set(bus, "pending_frame", request);
        }

        private static bool Expected(ModBus bus, byte[] frame)
        {
            return (bool)Invoke(bus, "IsExpectedResponse", frame);
        }

        private static void TestResponseValidation()
        {
            var bus = new ModBus();
            byte[] readReq = WithCrc(bus, 0x11, 0x03, 0x00, 0x00, 0x00, 0x02);
            PreparePending(bus, 0x11, 0x03, readReq);

            byte[] goodRead = WithCrc(bus, 0x11, 0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD);
            Assert(Expected(bus, goodRead), "matching FC03 response accepted");

            byte[] wrongSlave = (byte[])goodRead.Clone();
            wrongSlave[0] = 0x12;
            Assert(!Expected(bus, wrongSlave), "wrong slave rejected");

            byte[] wrongCount = WithCrc(bus, 0x11, 0x03, 0x02, 0x12, 0x34);
            Assert(!Expected(bus, wrongCount), "wrong read byte count rejected");

            byte[] exception = WithCrc(bus, 0x11, 0x83, 0x02);
            Assert(exception.Length == 5 && Expected(bus, exception), "five-byte exception response accepted");

            byte[] writeReq = WithCrc(bus, 0x11, 0x06, 0x00, 0x01, 0x00, 0x03);
            PreparePending(bus, 0x11, 0x06, writeReq);
            Assert(Expected(bus, writeReq), "matching FC06 echo accepted");

            byte[] badEcho = WithCrc(bus, 0x11, 0x06, 0x00, 0x01, 0x00, 0x04);
            Assert(!Expected(bus, badEcho), "mismatched FC06 echo rejected");
        }

        private static void TestDecodedData()
        {
            var registerResponse = new ModBus.ReadResponseArgs
            {
                pdu = new byte[] { 1, 3, 4, 0x12, 0x34, 0xAB, 0xCD, 0, 0 },
                ex_resp = false
            };
            InvokeStatic("DecodeReadData", registerResponse, (byte)0x03);
            Assert(registerResponse.registers.Length == 2, "register response decoded count");
            Assert(registerResponse.registers[0] == 0x1234 && registerResponse.registers[1] == 0xABCD,
                "register response decoded as ushort big-endian");

            var coilResponse = new ModBus.ReadResponseArgs
            {
                pdu = new byte[] { 1, 1, 1, 0x05, 0, 0 },
                ex_resp = false
            };
            InvokeStatic("DecodeReadData", coilResponse, (byte)0x01);
            Assert(coilResponse.bits[0] && !coilResponse.bits[1] && coilResponse.bits[2],
                "coil bits decoded LSB-first");
        }

        private static void ExpectArgumentFailure(Action action, string name)
        {
            try
            {
                action();
                Assert(false, name);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException)
            {
                Assert(true, name);
            }
            catch (ArgumentException)
            {
                Assert(true, name);
            }
        }

        private static void TestLimits()
        {
            InvokeStatic("ValidateRequest", (byte)1, (ushort)0, (ushort)2000, 1, 2000);
            Assert(true, "maximum FC01 quantity accepted");
            InvokeStatic("ValidateRequest", (byte)247, (ushort)0, (ushort)125, 1, 125);
            Assert(true, "maximum slave ID and FC03 quantity accepted");

            ExpectArgumentFailure(() => InvokeStatic("ValidateSlaveId", (byte)0), "slave ID 0 rejected");
            ExpectArgumentFailure(() => InvokeStatic("ValidateRequest", (byte)1, (ushort)0, (ushort)2001, 1, 2000),
                "FC01 quantity above 2000 rejected");
            ExpectArgumentFailure(() => InvokeStatic("ValidateRequest", (byte)1, (ushort)0, (ushort)124, 1, 123),
                "FC10 quantity above 123 rejected");
            ExpectArgumentFailure(() => InvokeStatic("ValidateRequest", (byte)1, (ushort)65535, (ushort)2, 1, 125),
                "address range overflow rejected");
        }

        private static int Main()
        {
            TestCrcAndSerialization();
            TestResponseValidation();
            TestDecodedData();
            TestLimits();

            Console.WriteLine();
            Console.WriteLine("Passed: " + passed + ", Failed: " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
