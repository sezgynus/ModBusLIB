using System;
using System.Reflection;
using ModBusLIB;

namespace ModBusLIB.Tests
{
    internal static class Program
    {
        private sealed class FakeTransport : IModbusTransport
        {
            private readonly System.Collections.Generic.Queue<byte> input = new System.Collections.Generic.Queue<byte>();
            public event EventHandler DataReceived;
            public bool IsOpen { get; private set; }
            public string PortName { get { return "FAKE"; } }
            public int BytesToRead { get { return input.Count; } }
            public int WriteCount { get; private set; }
            public bool FailWrites { get; set; }
            public bool FailOpen { get; set; }
            public bool FailClose { get; set; }
            public bool FailDispose { get; set; }
            public bool Disposed { get; private set; }

            public void Open()
            {
                if (FailOpen) throw new System.IO.IOException("simulated open failure");
                IsOpen = true;
            }
            public void Close()
            {
                IsOpen = false;
                if (FailClose) throw new System.IO.IOException("simulated close failure");
            }
            public void Dispose()
            {
                Disposed = true;
                IsOpen = false;
                if (FailDispose) throw new System.IO.IOException("simulated dispose failure");
            }
            public void Write(byte[] buffer, int offset, int count)
            {
                if (FailWrites) throw new System.IO.IOException("simulated write failure");
                WriteCount++;
            }
            public int ReadByte() { return input.Dequeue(); }
            public void DiscardInBuffer() { input.Clear(); }
            public void Inject(params byte[] bytes)
            {
                foreach (byte value in bytes) input.Enqueue(value);
                EventHandler handler = DataReceived;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

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
                Frame = new byte[] { 1, 3, 4, 0x12, 0x34, 0xAB, 0xCD, 0, 0 },
                IsException = false
            };
            InvokeStatic("DecodeReadData", registerResponse, (byte)0x03, (ushort)0);
            Assert(registerResponse.Registers.Length == 2, "register response decoded count");
            Assert(registerResponse.Registers[0] == 0x1234 && registerResponse.Registers[1] == 0xABCD,
                "register response decoded as ushort big-endian");

            var coilResponse = new ModBus.ReadResponseArgs
            {
                Frame = new byte[] { 1, 1, 1, 0x05, 0, 0 },
                IsException = false
            };
            InvokeStatic("DecodeReadData", coilResponse, (byte)0x01, (ushort)0);
            Assert(coilResponse.Bits[0] && !coilResponse.Bits[1] && coilResponse.Bits[2],
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


        private static void ExpectInvalidOperation(Action action, string name)
        {
            try
            {
                action();
                Assert(false, name);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
                Assert(true, name);
            }
            catch (InvalidOperationException)
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


        private static void TestAdditionalProtocolCoverage()
        {
            var bus = new ModBus();

            byte[] expectedReadFunctions = { 0x01, 0x02, 0x03, 0x04 };
            foreach (byte function in expectedReadFunctions)
            {
                Invoke(bus, "modbus_read_serializer", function, (byte)0xF7, (ushort)0x1234, (ushort)1);
                byte[] tx = Get<byte[]>(bus, "tx_buf");
                Assert(tx.Length == 8 && tx[0] == 0xF7 && tx[1] == function &&
                    tx[2] == 0x12 && tx[3] == 0x34 && tx[4] == 0x00 && tx[5] == 0x01,
                    "FC" + function.ToString("X2") + " read serializer fields");
                Assert(Crc(bus, new byte[] { tx[0], tx[1], tx[2], tx[3], tx[4], tx[5] }) ==
                    (ushort)(tx[6] | (tx[7] << 8)), "FC" + function.ToString("X2") + " read serializer CRC");
            }

            byte[] coilData = { 0x55, 0x01 };
            int coilLen = (int)Invoke(bus, "modbus_write_serializer", (byte)0x0F, (byte)1,
                (ushort)0x0010, (ushort)9, coilData, null);
            byte[] coilTx = Get<byte[]>(bus, "tx_buf");
            Assert(coilLen == 11 && coilTx[6] == 2 && coilTx[7] == 0x55 && coilTx[8] == 0x01,
                "FC0F packed coil byte count and payload");

            ushort[] regData = { 0x1234, 0xABCD };
            int regLen = (int)Invoke(bus, "modbus_write_serializer", (byte)0x10, (byte)1,
                (ushort)0x0020, (ushort)2, null, regData);
            byte[] regTx = Get<byte[]>(bus, "tx_buf");
            Assert(regLen == 13 && regTx[6] == 4 &&
                regTx[7] == 0x12 && regTx[8] == 0x34 && regTx[9] == 0xAB && regTx[10] == 0xCD,
                "FC10 register payload is big-endian");

            byte[] functions = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x0F, 0x10 };
            foreach (byte function in functions)
            {
                byte[] request;
                byte[] response;
                if (function >= 0x01 && function <= 0x04)
                {
                    ushort quantity = (function <= 0x02) ? (ushort)8 : (ushort)1;
                    request = WithCrc(bus, 0x22, function, 0x00, 0x00, 0x00, (byte)quantity);
                    response = (function <= 0x02)
                        ? WithCrc(bus, 0x22, function, 0x01, 0x5A)
                        : WithCrc(bus, 0x22, function, 0x02, 0x12, 0x34);
                }
                else
                {
                    request = WithCrc(bus, 0x22, function, 0x00, 0x10, 0x00, 0x01);
                    response = (byte[])request.Clone();
                }

                PreparePending(bus, 0x22, function, request);
                Assert(Expected(bus, response), "FC" + function.ToString("X2") + " normal response matching");

                PreparePending(bus, 0x22, function, request);
                byte[] exception = WithCrc(bus, 0x22, (byte)(function | 0x80), 0x02);
                Assert(Expected(bus, exception), "FC" + function.ToString("X2") + " exception response matching");
            }

            byte[] fc03Req = WithCrc(bus, 0x22, 0x03, 0, 0, 0, 1);
            PreparePending(bus, 0x22, 0x03, fc03Req);
            Assert(!Expected(bus, WithCrc(bus, 0x22, 0x04, 0x02, 0, 1)), "wrong function rejected");
            Assert(!Expected(bus, new byte[] { 0x22, 0x03, 0, 0 }), "response shorter than five bytes rejected");

            var discrete = new ModBus.ReadResponseArgs
            {
                Frame = new byte[] { 1, 2, 1, 0xA5, 0, 0 },
                IsException = false
            };
            InvokeStatic("DecodeReadData", discrete, (byte)0x02, (ushort)0);
            Assert(discrete.Bits.Length == 8 && discrete.Bits[0] && !discrete.Bits[1] && discrete.Bits[2],
                "FC02 discrete inputs decoded LSB-first");

            var inputRegs = new ModBus.ReadResponseArgs
            {
                Frame = new byte[] { 1, 4, 2, 0xBE, 0xEF, 0, 0 },
                IsException = false
            };
            InvokeStatic("DecodeReadData", inputRegs, (byte)0x04, (ushort)0);
            Assert(inputRegs.Registers.Length == 1 && inputRegs.Registers[0] == 0xBEEF,
                "FC04 input register decoded big-endian");

            var exceptionData = new ModBus.ReadResponseArgs
            {
                Frame = new byte[] { 1, 0x83, 2, 0, 0 },
                IsException = true
            };
            InvokeStatic("DecodeReadData", exceptionData, (byte)0x03, (ushort)0);
            Assert(exceptionData.Registers == null && exceptionData.Bits == null,
                "exception response is not decoded as normal data");

            InvokeStatic("ValidateRequest", (byte)1, (ushort)65535, (ushort)1, 1, 125);
            Assert(true, "last Modbus address with quantity one accepted");
            ExpectArgumentFailure(() => InvokeStatic("ValidateSlaveId", (byte)248), "slave ID 248 rejected");
            ExpectArgumentFailure(() => InvokeStatic("ValidateRequest", (byte)1, (ushort)0, (ushort)0, 1, 125),
                "zero quantity rejected");
        }


        private static void TestClosedPortRequestRejected()
        {
            var bus = new ModBus();
            Set(bus, "tx_buf", new byte[8]);
            ExpectInvalidOperation(() => Invoke(bus, "SendRequest", (byte)1, (byte)3, 8),
                "request on closed serial port rejected immediately");
            Assert(!Get<bool>(bus, "request_pending"), "closed-port request does not become pending");
        }


        private static void TestCloseClearsPendingRequest()
        {
            var bus = new ModBus();
            Set(bus, "request_pending", true);
            Set(bus, "pending_frame", new byte[] { 1, 3, 0, 0, 0, 1, 0, 0 });
            Set(bus, "pending_retry_count", 2);
            Set(bus, "pending_since_ms", 123L);
            bus.Close();
            Assert(!Get<bool>(bus, "request_pending"), "Close clears pending request flag");
            Assert(Get<byte[]>(bus, "pending_frame") == null, "Close clears pending request frame");
            Assert(Get<int>(bus, "pending_retry_count") == 0, "Close clears retry count");
            Assert(Get<long>(bus, "pending_since_ms") == 0L, "Close clears pending timestamp");
        }


        private static void TestReinitializeRejected()
        {
            var bus = new ModBus();
            Set(bus, "us_timer_flag", true);
            ExpectInvalidOperation(() => bus.Initialize("COM1"), "Initialize rejects an already active instance");
            Set(bus, "us_timer_flag", false);
        }


        private static void TestTimeoutConfigurationValidation()
        {
            var bus = new ModBus();
            bus.ResponseTimeoutMs = 250;
            bus.MaxRetries = 3;
            Assert(bus.ResponseTimeoutMs == 250, "positive response timeout accepted");
            Assert(bus.MaxRetries == 3, "non-negative retry count accepted");
            ExpectArgumentFailure(() => bus.ResponseTimeoutMs = 0, "zero response timeout rejected");
            ExpectArgumentFailure(() => bus.ResponseTimeoutMs = -1, "negative response timeout rejected");
            ExpectArgumentFailure(() => bus.MaxRetries = -1, "negative retry count rejected");
        }


        private static void TestPacketDispatch()
        {
            var bus = new ModBus();
            byte[] request = WithCrc(bus, 0x31, 0x03, 0, 0, 0, 1);
            PreparePending(bus, 0x31, 0x03, request);
            int events = 0;
            ushort value = 0;
            bus.ReadHoldingRegistersResponseHandler += (sender, e) =>
            {
                events++;
                value = e.Registers[0];
            };
            Invoke(bus, "ProcessPacket", WithCrc(bus, 0x31, 0x03, 0x02, 0x12, 0x34));
            Assert(events == 1 && value == 0x1234, "validated packet dispatches decoded response event");
            Assert(!Get<bool>(bus, "request_pending"), "validated packet clears pending request");

            PreparePending(bus, 0x31, 0x03, request);
            byte[] badCrc = WithCrc(bus, 0x31, 0x03, 0x02, 0x12, 0x34);
            badCrc[badCrc.Length - 1] ^= 0xFF;
            Invoke(bus, "ProcessPacket", badCrc);
            Assert(events == 1, "CRC-invalid packet does not dispatch response event");
            Assert(bus.CrcFailCount == 1, "CRC-invalid packet increments failure count");
        }


        private static void TestTimeoutAndExceptionLifecycle()
        {
            var timeoutBus = new ModBus();
            timeoutBus.ResponseTimeoutMs = 1;
            byte[] request = WithCrc(timeoutBus, 0x41, 0x03, 0, 0, 0, 1);
            PreparePending(timeoutBus, 0x41, 0x03, request);
            Set(timeoutBus, "pending_since_ms", -1000L);
            int timeoutEvents = 0;
            byte timeoutSlave = 0;
            byte timeoutFunction = 0;
            int timeoutRetries = -1;
            timeoutBus.RequestTimeoutHandler += (sender, e) =>
            {
                timeoutEvents++;
                timeoutSlave = e.SlaveId;
                timeoutFunction = e.Function;
                timeoutRetries = e.Retries;
            };
            Invoke(timeoutBus, "CheckRequestTimeout");
            Assert(timeoutEvents == 1 && timeoutSlave == 0x41 && timeoutFunction == 0x03 && timeoutRetries == 0,
                "expired request raises timeout event with request metadata");
            Assert(!Get<bool>(timeoutBus, "request_pending") && Get<byte[]>(timeoutBus, "pending_frame") == null,
                "timeout clears pending request state");

            var retryBus = new ModBus();
            retryBus.ResponseTimeoutMs = 1;
            retryBus.MaxRetries = 1;
            PreparePending(retryBus, 0x42, 0x03, WithCrc(retryBus, 0x42, 0x03, 0, 0, 0, 1));
            Set(retryBus, "pending_since_ms", -1000L);
            int retryTimeouts = 0;
            int reportedRetries = -1;
            retryBus.RequestTimeoutHandler += (sender, e) =>
            {
                retryTimeouts++;
                reportedRetries = e.Retries;
            };
            Invoke(retryBus, "CheckRequestTimeout");
            Assert(Get<int>(retryBus, "pending_retry_count") == 0 && Get<bool>(retryBus, "request_pending"),
                "unavailable transport does not consume a retry");
            Assert(retryTimeouts == 0, "unavailable transport keeps request pending for a future retry");

            var exceptionBus = new ModBus();
            byte[] exceptionRequest = WithCrc(exceptionBus, 0x51, 0x03, 0, 0, 0, 1);
            PreparePending(exceptionBus, 0x51, 0x03, exceptionRequest);
            int exceptionEvents = 0;
            byte exceptionCode = 0;
            exceptionBus.ReadHoldingRegistersResponseHandler += (sender, e) =>
            {
                exceptionEvents++;
                if (e.IsException)
                    exceptionCode = e.ExceptionCode;
            };
            Invoke(exceptionBus, "ProcessPacket", WithCrc(exceptionBus, 0x51, 0x83, 0x02));
            Assert(exceptionEvents == 1 && exceptionCode == 0x02,
                "Modbus exception packet dispatches exception metadata");
            Assert(!Get<bool>(exceptionBus, "request_pending"), "exception response completes pending request");

            var malformedBus = new ModBus();
            PreparePending(malformedBus, 0x61, 0x03, WithCrc(malformedBus, 0x61, 0x03, 0, 0, 0, 1));
            int malformedEvents = 0;
            malformedBus.ReadHoldingRegistersResponseHandler += (sender, e) => malformedEvents++;
            Invoke(malformedBus, "ProcessPacket", new byte[] { 0x61, 0x03, 0x00, 0x00 });
            Assert(malformedEvents == 0 && Get<bool>(malformedBus, "request_pending"),
                "malformed short packet is ignored without completing request");
        }


        private static void TestResponseHandlerExceptionIsolation()
        {
            var bus = new ModBus();
            int called = 0;
            bus.ReadHoldingRegistersResponseHandler += (sender, e) => { throw new InvalidOperationException("consumer failure"); };
            bus.ReadHoldingRegistersResponseHandler += (sender, e) => { called++; };

            byte[] request = WithCrc(bus, 1, 3, 0, 0, 0, 1);
            PreparePending(bus, 1, 3, request);
            byte[] response = WithCrc(bus, 1, 3, 2, 0x12, 0x34);
            Invoke(bus, "ProcessPacket", response);

            Assert(called == 1, "response callback failure does not block later subscribers");
        }


        private static void TestTimeoutHandlerExceptionIsolation()
        {
            var bus = new ModBus();
            int called = 0;
            bus.RequestTimeoutHandler += (sender, e) => { throw new InvalidOperationException("consumer failure"); };
            bus.RequestTimeoutHandler += (sender, e) => { called++; };

            var args = new ModBus.RequestTimeoutArgs { SlaveId = 1, Function = 3, Retries = 0 };
            FieldInfo eventField = typeof(ModBus).GetField("RequestTimeoutHandler", BindingFlags.Instance | BindingFlags.NonPublic);
            var handler = (EventHandler<ModBus.RequestTimeoutArgs>)eventField.GetValue(bus);
            MethodInfo safeInvoke = typeof(ModBus).GetMethod(
                "SafeInvoke",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(EventHandler<ModBus.RequestTimeoutArgs>), typeof(ModBus.RequestTimeoutArgs) },
                null);
            safeInvoke.Invoke(bus, new object[] { handler, args });

            Assert(called == 1, "timeout callback failure does not block later subscribers");
        }


        private static void TestRequestedCoilQuantityTrimsPaddingBits()
        {
            var bus = new ModBus();
            byte[] request = WithCrc(bus, 1, 1, 0, 0, 0, 9);
            PreparePending(bus, 1, 1, request);
            int bitCount = -1;
            bus.ReadCoilsResponseHandler += (sender, e) => bitCount = e.Bits.Length;
            Invoke(bus, "ProcessPacket", WithCrc(bus, 1, 1, 2, 0x55, 0x01));
            Assert(bitCount == 9, "FC01 decoded bits match requested quantity instead of padded byte size");

            var discreteBus = new ModBus();
            byte[] discreteRequest = WithCrc(discreteBus, 1, 2, 0, 0, 0, 9);
            PreparePending(discreteBus, 1, 2, discreteRequest);
            int discreteBitCount = -1;
            discreteBus.ReadDiscreteInputsResponseHandler += (sender, e) => discreteBitCount = e.Bits.Length;
            Invoke(discreteBus, "ProcessPacket", WithCrc(discreteBus, 1, 2, 2, 0xAA, 0x01));
            Assert(discreteBitCount == 9, "FC02 decoded bits match requested quantity instead of padded byte size");
        }


        private static void TestCallbackExceptionDiagnostics()
        {
            var bus = new ModBus();
            int diagnostics = 0;
            string callbackName = null;
            Exception captured = null;
            bus.CallbackExceptionHandler += (sender, e) =>
            {
                diagnostics++;
                callbackName = e.CallbackName;
                captured = e.Exception;
            };
            bus.ReadHoldingRegistersResponseHandler += (sender, e) => { throw new InvalidOperationException("consumer failure"); };

            byte[] request = WithCrc(bus, 1, 3, 0, 0, 0, 1);
            PreparePending(bus, 1, 3, request);
            Invoke(bus, "ProcessPacket", WithCrc(bus, 1, 3, 2, 0, 1));

            Assert(diagnostics == 1 && callbackName == "ReadHoldingRegistersResponseHandler" &&
                captured is InvalidOperationException, "response callback exceptions are exposed through diagnostics");
        }


        private static void TestResponseMetadataAndFrameIsolation()
        {
            var bus = new ModBus();
            byte[] request = WithCrc(bus, 7, 3, 0x12, 0x34, 0, 2);
            PreparePending(bus, 7, 3, request);

            ModBus.ReadResponseArgs captured = null;
            bus.ReadHoldingRegistersResponseHandler += (sender, e) => captured = e;
            byte[] response = WithCrc(bus, 7, 3, 4, 0x11, 0x22, 0x33, 0x44);
            Invoke(bus, "ProcessPacket", response);

            Assert(captured != null && captured.Function == 3 && captured.StartAddress == 0x1234 &&
                captured.RequestedQuantity == 2, "response exposes request function address and quantity metadata");

            byte[] first = captured.Frame;
            first[0] = 0xFF;
            byte[] second = captured.Frame;
            Assert(second[0] == 7, "response Frame getter returns a defensive snapshot");

            response[1] = 0xFF;
            Assert(captured.Frame[1] == 3, "response Frame is isolated from source packet mutations");

            var writeBus = new ModBus();
            byte[] writeRequest = WithCrc(writeBus, 9, 6, 0x00, 0x20, 0x12, 0x34);
            PreparePending(writeBus, 9, 6, writeRequest);
            ModBus.ReadResponseArgs writeCaptured = null;
            writeBus.WriteSingleRegisterResponseHandler += (sender, e) => writeCaptured = e;
            Invoke(writeBus, "ProcessPacket", (byte[])writeRequest.Clone());
            Assert(writeCaptured != null && writeCaptured.Function == 6 &&
                writeCaptured.StartAddress == 0x0020 && writeCaptured.RequestedQuantity == 1,
                "single-write response metadata reports quantity one");
        }

        private static void TestTransportAbstraction()
        {
            var transport = new FakeTransport();
            var bus = new ModBus { ResponseTimeoutMs = 1, MaxRetries = 1 };
            bus.InitializeTransport(transport, 115200);
            Assert(bus.IsOpen && bus.PortName == "FAKE", "controlled transport state is exposed without SerialPort access");

            bus.ReadHoldingRegisters(1, 0, 1);
            Assert(transport.WriteCount == 1, "initial request is written through transport abstraction");

            Set(bus, "pending_since_ms", -1000L);
            Invoke(bus, "CheckRequestTimeout");
            Assert(transport.WriteCount == 2 && Get<int>(bus, "pending_retry_count") == 1,
                "successful retry is deterministic through fake transport");

            byte[] response = WithCrc(bus, 1, 3, 2, 0x12, 0x34);
            int responses = 0;
            bus.ReadHoldingRegistersResponseHandler += (sender, e) => responses++;
            transport.Inject(response);
            Set(bus, "last_rx_us", long.MinValue / 2);
            Invoke(bus, "modbus_timer_Tick");
            Assert(responses == 1, "injected RX frame is processed through transport abstraction");

            bus.Close();
            Assert(!bus.IsOpen && bus.PortName == null && transport.Disposed, "Close disposes and detaches transport");

            var failedTransport = new FakeTransport { FailWrites = true };
            var failedBus = new ModBus { ResponseTimeoutMs = 1, MaxRetries = 1 };
            failedBus.InitializeTransport(failedTransport, 115200);
            try { failedBus.ReadHoldingRegisters(1, 0, 1); } catch (System.IO.IOException) { }
            Assert(!Get<bool>(failedBus, "request_pending"), "failed initial transport write rolls back pending request");
            failedBus.Close();
        }

        private static void TestTransportLifecycleFailures()
        {
            var openTransport = new FakeTransport { FailOpen = true };
            var openBus = new ModBus();
            bool openFailed = false;
            try { openBus.InitializeTransport(openTransport, 115200); }
            catch (System.IO.IOException) { openFailed = true; }
            Assert(openFailed && !openBus.IsOpen && openBus.PortName == null && openTransport.Disposed,
                "Initialize rollback disposes transport after open failure");

            var closeTransport = new FakeTransport { FailClose = true };
            var closeBus = new ModBus();
            closeBus.InitializeTransport(closeTransport, 115200);
            bool closeFailed = false;
            try { closeBus.Close(); }
            catch (System.IO.IOException) { closeFailed = true; }
            Assert(closeFailed && !closeBus.IsOpen && closeBus.PortName == null && closeTransport.Disposed,
                "Close completes cleanup and rethrows transport close failure");

            var disposeTransport = new FakeTransport { FailDispose = true };
            var disposeBus = new ModBus();
            disposeBus.InitializeTransport(disposeTransport, 115200);
            bool disposeFailed = false;
            try { disposeBus.Close(); }
            catch (System.IO.IOException) { disposeFailed = true; }
            Assert(disposeFailed && !disposeBus.IsOpen && disposeBus.PortName == null && disposeTransport.Disposed,
                "Close clears library state when transport dispose fails");
        }

        private static int Main()
        {
            TestCrcAndSerialization();
            TestResponseValidation();
            TestDecodedData();
            TestLimits();
            TestAdditionalProtocolCoverage();
            TestClosedPortRequestRejected();
            TestCloseClearsPendingRequest();
            TestReinitializeRejected();
            TestTimeoutConfigurationValidation();
            TestPacketDispatch();
            TestTimeoutAndExceptionLifecycle();
            TestResponseHandlerExceptionIsolation();
            TestTimeoutHandlerExceptionIsolation();
            TestRequestedCoilQuantityTrimsPaddingBits();
            TestCallbackExceptionDiagnostics();
            TestResponseMetadataAndFrameIsolation();
            TestTransportAbstraction();
            TestTransportLifecycleFailures();

            Console.WriteLine();
            Console.WriteLine("Passed: " + passed + ", Failed: " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
