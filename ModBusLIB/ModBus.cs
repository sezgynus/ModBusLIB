using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;

namespace ModBusLIB
{
    internal interface IModbusTransport : IDisposable
    {
        event EventHandler DataReceived;
        bool IsOpen { get; }
        string PortName { get; }
        int BytesToRead { get; }
        void Open();
        void Close();
        void Write(byte[] buffer, int offset, int count);
        int ReadByte();
        void DiscardInBuffer();
    }
    internal sealed class SerialPortTransport : IModbusTransport
    {
        private readonly SerialPort port;

        public event EventHandler DataReceived;

        public SerialPortTransport(string portName, int baudRate, Parity parity, StopBits stopBits)
        {
            port = new SerialPort(portName, baudRate, parity, 8, stopBits);
            port.DataReceived += OnDataReceived;
        }

        public bool IsOpen { get { return port.IsOpen; } }
        public string PortName { get { return port.PortName; } }
        public int BytesToRead { get { return port.BytesToRead; } }

        public void Open() { port.Open(); }
        public void Close() { port.Close(); }
        public void Write(byte[] buffer, int offset, int count) { port.Write(buffer, offset, count); }
        public int ReadByte() { return port.ReadByte(); }
        public void DiscardInBuffer() { port.DiscardInBuffer(); }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            EventHandler handler = DataReceived;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            port.DataReceived -= OnDataReceived;
            port.Dispose();
        }
    }

    public class ModBus
    {
        private IModbusTransport transport;
        public bool IsOpen { get { return transport != null && transport.IsOpen; } }
        public string PortName { get { return transport == null ? null : transport.PortName; } }
        private int crcFailCount;
        public int CrcFailCount { get { return Volatile.Read(ref crcFailCount); } }
        private long t3_5;
        private Stopwatch microtimer = new Stopwatch();
        private byte[] rx_buf, tx_buf;
        private int rx_buf_index = 0;
        private bool us_timer_flag = false;
        private bool new_packet = false;
        private long last_rx_us;
        private Thread us_timer;
        private readonly object rx_lock = new object();
        private readonly object request_lock = new object();
        private bool request_pending;
        private byte pending_slave_id;
        private byte pending_function;
        private byte[] pending_frame;
        private long pending_since_ms;
        private int pending_retry_count;
        private int responseTimeoutMs = 1000;
        private int maxRetries;

        public int ResponseTimeoutMs
        {
            get { return responseTimeoutMs; }
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Response timeout must be greater than zero.");
                responseTimeoutMs = value;
            }
        }

        public int MaxRetries
        {
            get { return maxRetries; }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Maximum retries cannot be negative.");
                maxRetries = value;
            }
        }

        public event EventHandler<ReadResponseArgs> ReadCoilsResponseHandler;
        public event EventHandler<ReadResponseArgs> ReadDiscreteInputsResponseHandler;
        public event EventHandler<ReadResponseArgs> ReadHoldingRegistersResponseHandler;
        public event EventHandler<ReadResponseArgs> ReadInputRegistersResponseHandler;
        public event EventHandler<ReadResponseArgs> WriteSingleCoilResponseHandler;
        public event EventHandler<ReadResponseArgs> WriteMultipleCoilsResponseHandler;
        public event EventHandler<ReadResponseArgs> WriteSingleRegisterResponseHandler;
        public event EventHandler<ReadResponseArgs> WriteMultipleRegistersResponseHandler;
        public event EventHandler<RequestTimeoutArgs> RequestTimeoutHandler;
        public event EventHandler<CallbackExceptionArgs> CallbackExceptionHandler;

        public sealed class CallbackExceptionArgs : EventArgs
        {
            public Exception Exception { get; private set; }
            public string CallbackName { get; private set; }

            public CallbackExceptionArgs(Exception exception, string callbackName)
            {
                Exception = exception;
                CallbackName = callbackName;
            }
        }

        public sealed class RequestTimeoutArgs : EventArgs
        {
            public byte SlaveId { get; set; }
            public byte Function { get; set; }
            public int Retries { get; set; }
        }

        private void SendRequest(byte slaveId, byte function, int packetSize)
        {
            lock (request_lock)
            {
                if (transport == null || !transport.IsOpen)
                    throw new InvalidOperationException("Serial port is not open.");

                if (request_pending)
                    throw new InvalidOperationException("A Modbus request is already awaiting a response.");

                request_pending = true;
                pending_slave_id = slaveId;
                pending_function = function;
                pending_frame = new byte[packetSize];
                Array.Copy(tx_buf, pending_frame, packetSize);
                pending_since_ms = microtimer.ElapsedMilliseconds;
                pending_retry_count = 0;
                try
                {
                    if (transport != null && transport.IsOpen)
                        transport.Write(tx_buf, 0, packetSize);
                }
                catch
                {
                    request_pending = false;
                    throw;
                }
            }
        }
        public sealed class ReadResponseArgs : EventArgs
        {
            private byte[] frame;
            public byte[] Frame
            {
                get { return frame == null ? null : (byte[])frame.Clone(); }
                set { frame = value == null ? null : (byte[])value.Clone(); }
            }
            public bool CrcOk { get; set; }
            public byte SlaveId { get; set; }
            public byte Function { get; set; }
            public ushort StartAddress { get; set; }
            public ushort RequestedQuantity { get; set; }
            public bool IsException { get; set; }
            public byte ExceptionCode { get; set; }
            public bool[] Bits { get; set; }
            public ushort[] Registers { get; set; }

        }

        private static void DecodeReadData(ReadResponseArgs response, byte function, ushort requestedQuantity = 0)
        {
            byte[] frame = response.Frame;
            if (response.IsException || frame == null || frame.Length < 5)
                return;

            int byteCount = frame[2];
            if (function == 0x01 || function == 0x02)
            {
                int bitCount = requestedQuantity > 0 ? requestedQuantity : byteCount * 8;
                response.Bits = new bool[bitCount];
                for (int i = 0; i < response.Bits.Length; i++)
                    response.Bits[i] = (frame[3 + (i / 8)] & (1 << (i % 8))) != 0;
            }
            else if (function == 0x03 || function == 0x04)
            {
                response.Registers = new ushort[byteCount / 2];
                for (int i = 0; i < response.Registers.Length; i++)
                    response.Registers[i] = (ushort)((frame[3 + i * 2] << 8) | frame[4 + i * 2]);
            }
        }
        public void Close()
        {
            IModbusTransport currentTransport = transport;
            Exception transportException = null;

            try
            {
                if (currentTransport != null)
                    currentTransport.DataReceived -= serial_rx;
            }
            catch (Exception ex)
            {
                transportException = ex;
            }
            us_timer_flag = false;
            if (us_timer != null && us_timer.IsAlive && Thread.CurrentThread != us_timer)
                us_timer.Join(2000);

            lock (rx_lock)
            {
                new_packet = false;
                rx_buf_index = 0;
            }

            lock (request_lock)
            {
                request_pending = false;
                pending_frame = null;
                pending_retry_count = 0;
                pending_since_ms = 0;
            }
            try
            {
                if (currentTransport != null)
                {
                    try
                    {
                        if (currentTransport.IsOpen)
                            currentTransport.Close();
                    }
                    catch (Exception ex)
                    {
                        if (transportException == null)
                            transportException = ex;
                    }
                    finally
                    {
                        try
                        {
                            currentTransport.Dispose();
                        }
                        catch (Exception ex)
                        {
                            if (transportException == null)
                                transportException = ex;
                        }
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(transport, currentTransport))
                    transport = null;
                microtimer.Stop();
                microtimer.Reset();
                us_timer = null;
            }

            if (transportException != null)
                throw transportException;
        }
        public void Initialize(string portName, int baudRate=115200, Parity parity=Parity.Even)
        {
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("A serial port name is required.", nameof(portName));
            if (baudRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(baudRate));
            StopBits stopBits = parity == Parity.None ? StopBits.Two : StopBits.One;
            InitializeTransport(new SerialPortTransport(portName, baudRate, parity, stopBits), baudRate);
        }
        internal void InitializeTransport(IModbusTransport newTransport, int baudRate)
        {
            if (newTransport == null)
                throw new ArgumentNullException(nameof(newTransport));
            if (baudRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(baudRate));
            if (us_timer_flag || (transport != null && transport.IsOpen))
                throw new InvalidOperationException("ModBus is already initialized. Call Close() before initializing again.");
            t3_5 = baudRate > 19200 ? 1750 : 38500000 / baudRate;
            try
            {
                newTransport.DataReceived += serial_rx;
                newTransport.Open();
                transport = newTransport;
                rx_buf = new byte[4096];
                tx_buf = new byte[8];
                us_timer_flag = true;
                us_timer = new Thread(new ThreadStart(us_timer_task)) { IsBackground = true };
                us_timer.Start();
                microtimer.Start();
            }
            catch
            {
                us_timer_flag = false;
                if (us_timer != null && us_timer.IsAlive && Thread.CurrentThread != us_timer)
                    us_timer.Join(2000);
                try
                {
                    newTransport.DataReceived -= serial_rx;
                    if (newTransport.IsOpen)
                        newTransport.Close();
                }
                finally
                {
                    newTransport.Dispose();
                    if (ReferenceEquals(transport, newTransport))
                        transport = null;
                    us_timer = null;
                    microtimer.Stop();
                    microtimer.Reset();
                }
                throw;
            }
        }
        public void ReadCoils(byte slaveId, ushort start, ushort count)//0x01
        {
            ValidateRequest(slaveId, start, count, 1, 2000);
            modbus_read_serializer(0x01, slaveId, start, count);
            SendRequest(slaveId, 0x01, 8);
        }
        public void ReadDiscreteInputs(byte slaveId, ushort start, ushort count)//0x02
        {
            ValidateRequest(slaveId, start, count, 1, 2000);
            modbus_read_serializer(0x02, slaveId, start, count);
            SendRequest(slaveId, 0x02, 8);
        }
        public void ReadHoldingRegisters(byte slaveId, ushort start, ushort count)//0x03
        {
            ValidateRequest(slaveId, start, count, 1, 125);
            modbus_read_serializer(0x03, slaveId, start, count);
            SendRequest(slaveId, 0x03, 8);
        }
        public void ReadInputRegisters(byte slaveId, ushort start, ushort count)//0x04
        {
            ValidateRequest(slaveId, start, count, 1, 125);
            modbus_read_serializer(0x04, slaveId, start, count);
            SendRequest(slaveId, 0x04, 8);
        }
        public void WriteSingleCoil(byte slaveId, ushort address, bool coilValue)//0x05
        {
            ValidateSlaveId(slaveId);
            byte[] value = new byte[2];
            int packetSize;
            if (coilValue)
            {
                value[0] = 0xFF;
                value[1] = 0x00;
            }
            else
            {
                value[0] = 0x00;
                value[1] = 0x00;
            }
            packetSize = modbus_write_serializer(0x05, slaveId, address, 0, value);
            SendRequest(slaveId, 0x05, packetSize);
        }
        public int WriteMultipleCoils(byte slaveId, ushort start, ushort count, byte[] data)//0x0F
        {
            ValidateRequest(slaveId, start, count, 1, 1968);
            int requiredBytes = (count + 7) / 8;
            if (data == null || data.Length < requiredBytes)
                throw new ArgumentException("Packed coil data is shorter than the requested quantity.", nameof(data));
            int packetSize;
            packetSize = modbus_write_serializer(0x0F, slaveId, start, count, data);
            SendRequest(slaveId, 0x0F, packetSize);
            return packetSize;
        }
        public int WriteSingleRegister(byte slaveId, ushort address, ushort value)//0x06
        {
            ValidateSlaveId(slaveId);
            int packetSize;
            ushort[] values = { value };
            packetSize = modbus_write_serializer(0x06, slaveId, address, 0, null, values);
            SendRequest(slaveId, 0x06, packetSize);
            return packetSize;
        }
        public int WriteMultipleRegisters(byte slaveId, ushort start, ushort count, ushort[] data)//0x10
        {
            ValidateRequest(slaveId, start, count, 1, 123);
            if (data == null || data.Length < count)
                throw new ArgumentException("Register data is shorter than the requested quantity.", nameof(data));
            int packetSize;
            packetSize = modbus_write_serializer(0x10, slaveId, start, count, null, data);
            SendRequest(slaveId, 0x10, packetSize);
            return packetSize;
        }

        private static void ValidateSlaveId(byte slaveId)
        {
            if (slaveId < 1 || slaveId > 247)
                throw new ArgumentOutOfRangeException(nameof(slaveId), "Slave ID must be between 1 and 247.");
        }

        private static void ValidateRequest(byte slaveId, ushort start, ushort count, int minCount, int maxCount)
        {
            ValidateSlaveId(slaveId);
            if (count < minCount || count > maxCount)
                throw new ArgumentOutOfRangeException(nameof(count));
            if ((uint)start + count > 65536u)
                throw new ArgumentOutOfRangeException(nameof(count), "Address range exceeds the Modbus address space.");
        }

        private ushort CRC16_MODBUS(byte[] buf, int len)
        {
            ushort crc = 0xFFFF;
            int pos, i;
            for (pos = 0; pos < len; pos++)
            {
                crc ^= buf[pos];

                for (i = 8; i != 0; i--)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }

        private int modbus_read_serializer(byte function, byte slaveId, ushort start, ushort count)
        {
            int l = 8;
            tx_buf = new byte[l];
            if ((function == 0x01) || (function == 0x02) || (function == 0x03) || (function == 0x04))
            {
                tx_buf[0] = slaveId;
                tx_buf[1] = function;

                tx_buf[2] = (byte)(start >> 8);
                tx_buf[3] = (byte)start;

                tx_buf[4] = (byte)(count >> 8);
                tx_buf[5] = (byte)count;

                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);

                tx_buf[6] = (byte)calculated_crc;
                tx_buf[7] = (byte)(calculated_crc >> 8);
                l = 8;
            }
            return l;
        }
        private int modbus_write_serializer(byte function, byte slaveId, ushort start, ushort count, byte[] byteData = null, ushort[] registerData = null)
        {
            int l = 0;
            int frameLength = (function == 0x0F) ? 9 + ((count + 7) / 8)
                            : (function == 0x10) ? 9 + (count * 2)
                            : 8;
            tx_buf = new byte[frameLength];
            if (function == 0x05)
            {
                l = 8;
                tx_buf[0] = slaveId;
                tx_buf[1] = function;

                tx_buf[2] = (byte)(start >> 8);
                tx_buf[3] = (byte)start;

                tx_buf[4] = byteData[0];
                tx_buf[5] = byteData[1];
                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);
                tx_buf[l - 2] = (byte)calculated_crc;
                tx_buf[l - 1] = (byte)(calculated_crc >> 8);
                
            }
            else if (function == 0x0F)
            {
                tx_buf[0] = slaveId;
                tx_buf[1] = function;

                tx_buf[2] = (byte)(start >> 8);
                tx_buf[3] = (byte)start;

                tx_buf[4] = (byte)(count >> 8);
                tx_buf[5] = (byte)count;
                if ((count % 8) == 0) tx_buf[6] = (byte)(count / 8);
                else tx_buf[6] = (byte)((count / 8) + 1);
                l = 7;
                for (int i = 0; i < tx_buf[6]; i++)
                {
                    tx_buf[i + 7] = byteData[i];
                    l++;
                }
                l += 2;
                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);
                tx_buf[l - 2] = (byte)calculated_crc;
                tx_buf[l - 1] = (byte)(calculated_crc >> 8);
            }
            if (function == 0x06)
            {
                l = 8;
                tx_buf[0] = slaveId;
                tx_buf[1] = function;

                tx_buf[2] = (byte)(start >> 8);
                tx_buf[3] = (byte)start;

                tx_buf[4] = (byte)(registerData[0] >> 8);
                tx_buf[5] = (byte)registerData[0];
                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);
                tx_buf[l - 2] = (byte)calculated_crc;
                tx_buf[l - 1] = (byte)(calculated_crc >> 8);

            }
            if (function == 0x10)
            {
                l = 8;
                tx_buf[0] = slaveId;
                tx_buf[1] = function;

                tx_buf[2] = (byte)(start >> 8);
                tx_buf[3] = (byte)start;

                tx_buf[4] = (byte)(count >> 8);
                tx_buf[5] = (byte)count;
                tx_buf[6] = (byte)(count * 2);
                l = 7;
                for (int i = 0; i < count; i++)
                {
                    tx_buf[l] = (byte)(registerData[i] >> 8);
                    tx_buf[l + 1] = (byte)registerData[i];
                    l += 2;
                }
                l += 2;
                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);
                tx_buf[l - 2] = (byte)calculated_crc;
                tx_buf[l - 1] = (byte)(calculated_crc >> 8);

            }
            return l;
        }
        private void us_timer_task()
        {
            while (us_timer_flag)
            {
                modbus_timer_Tick();
                CheckRequestTimeout();
                WaitForNextWorkerIteration();
            }
        }

        private void WaitForNextWorkerIteration()
        {
            long remainingUs = -1;
            lock (rx_lock)
            {
                if (new_packet)
                {
                    long nowUs = (long)(((double)microtimer.ElapsedTicks / Stopwatch.Frequency) * 1000000);
                    remainingUs = t3_5 - (nowUs - last_rx_us);
                }
            }

            if (remainingUs < 0)
            {
                Thread.Sleep(1);
                return;
            }

            if (remainingUs > 2000)
            {
                Thread.Sleep((int)((remainingUs - 1000) / 1000));
                return;
            }

            if (remainingUs > 0)
            {
                long targetTicks = microtimer.ElapsedTicks +
                    (long)((remainingUs / 1000000.0) * Stopwatch.Frequency);
                while (us_timer_flag && microtimer.ElapsedTicks < targetTicks)
                    Thread.Yield();
            }
        }

        private bool TryCompleteExpectedResponse(byte[] packet, out ushort startAddress, out ushort requestedQuantity)
        {
            startAddress = 0;
            requestedQuantity = 0;

            lock (request_lock)
            {
                if (!request_pending || packet == null || packet.Length < 5)
                    return false;
                if (packet[0] != pending_slave_id || (packet[1] & 0x7F) != pending_function)
                    return false;

                bool exception = (packet[1] & 0x80) != 0;
                if (exception)
                {
                    if (packet.Length != 5)
                        return false;
                }
                else if (pending_function >= 0x01 && pending_function <= 0x04)
                {
                    ushort requestedCount = (ushort)((pending_frame[4] << 8) | pending_frame[5]);
                    int expectedBytes = (pending_function == 0x01 || pending_function == 0x02)
                        ? (requestedCount + 7) / 8
                        : requestedCount * 2;
                    if (packet[2] != expectedBytes || packet.Length != expectedBytes + 5)
                        return false;
                }
                else if (pending_function == 0x05 || pending_function == 0x06 ||
                    pending_function == 0x0F || pending_function == 0x10)
                {
                    if (packet.Length != 8)
                        return false;
                    for (int i = 2; i <= 5; i++)
                    {
                        if (packet[i] != pending_frame[i])
                            return false;
                    }
                }
                else
                {
                    return false;
                }

                if (pending_frame != null && pending_frame.Length >= 6)
                {
                    startAddress = (ushort)((pending_frame[2] << 8) | pending_frame[3]);
                    if (pending_function >= 0x01 && pending_function <= 0x04 ||
                        pending_function == 0x0F || pending_function == 0x10)
                    {
                        requestedQuantity = (ushort)((pending_frame[4] << 8) | pending_frame[5]);
                    }
                    else if (pending_function == 0x05 || pending_function == 0x06)
                    {
                        requestedQuantity = 1;
                    }
                }

                request_pending = false;
                pending_frame = null;
                return true;
            }
        }

        private void CheckRequestTimeout()
        {
            RequestTimeoutArgs timeout = null;
            byte[] retryFrame = null;

            lock (rx_lock)
            {
                if (new_packet)
                {
                    long nowUs = (long)(((double)microtimer.ElapsedTicks / Stopwatch.Frequency) * 1000000);
                    if ((nowUs - last_rx_us) < t3_5)
                        return;
                }
            }

            lock (request_lock)
            {
                if (!request_pending || ResponseTimeoutMs <= 0)
                    return;

                if ((microtimer.ElapsedMilliseconds - pending_since_ms) < ResponseTimeoutMs)
                    return;

                if (pending_retry_count < MaxRetries)
                {
                    retryFrame = pending_frame;
                }
                else
                {
                    timeout = new RequestTimeoutArgs
                    {
                        SlaveId = pending_slave_id,
                        Function = pending_function,
                        Retries = pending_retry_count
                    };
                    request_pending = false;
                    pending_frame = null;
                }
            }

            if (retryFrame != null)
            {
                bool sent = false;
                try
                {
                    IModbusTransport currentTransport = transport;
                    if (currentTransport != null && currentTransport.IsOpen)
                    {
                        currentTransport.Write(retryFrame, 0, retryFrame.Length);
                        sent = true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Leave retry state unchanged; a retry is consumed only after a successful write.
                }
                catch (System.IO.IOException)
                {
                    // Leave retry state unchanged; the request remains pending for another attempt.
                }
                catch (UnauthorizedAccessException)
                {
                    // Leave retry state unchanged if the transport disappeared.
                }

                if (sent)
                {
                    lock (request_lock)
                    {
                        if (request_pending && ReferenceEquals(pending_frame, retryFrame))
                        {
                            pending_retry_count++;
                            pending_since_ms = microtimer.ElapsedMilliseconds;
                        }
                    }
                }
                return;
            }

            if (timeout != null)
                SafeInvoke(RequestTimeoutHandler, timeout);
        }

        private void serial_rx(object sender, EventArgs e)
        {
            try
            {
                lock (rx_lock)
                {
                    if (transport == null || !transport.IsOpen)
                        return;

                    new_packet = true;
                    int length = transport.BytesToRead;
                    for (int i = 0; i < length; i++)
                    {
                        if (rx_buf_index >= rx_buf.Length)
                        {
                            rx_buf_index = 0;
                            new_packet = false;
                            transport.DiscardInBuffer();
                            break;
                        }

                        rx_buf[rx_buf_index++] = (byte)transport.ReadByte();
                        last_rx_us = (long)(((double)microtimer.ElapsedTicks / Stopwatch.Frequency) * 1000000);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                ResetReceiveState();
            }
            catch (System.IO.IOException)
            {
                ResetReceiveState();
            }
            catch (UnauthorizedAccessException)
            {
                ResetReceiveState();
            }
        }

        private void ResetReceiveState()
        {
            lock (rx_lock)
            {
                rx_buf_index = 0;
                new_packet = false;
            }
        }

        private void modbus_timer_Tick()
        {
            byte[] packet = null;

            lock (rx_lock)
            {
                if (!new_packet)
                    return;

                long nowUs = (long)(((double)microtimer.ElapsedTicks / Stopwatch.Frequency) * 1000000);
                if ((nowUs - last_rx_us) < t3_5)
                    return;

                if (rx_buf_index < 5)
                {
                    rx_buf_index = 0;
                    new_packet = false;
                    return;
                }

                packet = new byte[rx_buf_index];
                Array.Copy(rx_buf, packet, rx_buf_index);
                rx_buf_index = 0;
                new_packet = false;
            }

            ProcessPacket(packet);
        }

        private void ReportCallbackException(Exception exception, string callbackName)
        {
            EventHandler<CallbackExceptionArgs> handler = CallbackExceptionHandler;
            if (handler == null)
                return;

            var args = new CallbackExceptionArgs(exception, callbackName);
            foreach (EventHandler<CallbackExceptionArgs> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, args);
                }
                catch
                {
                    // Diagnostic callbacks must never terminate the Modbus worker thread.
                }
            }
        }

        private void SafeInvoke(EventHandler<RequestTimeoutArgs> handler, RequestTimeoutArgs timeout)
        {
            if (handler == null)
                return;

            foreach (EventHandler<RequestTimeoutArgs> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, timeout);
                }
                catch (Exception ex)
                {
                    ReportCallbackException(ex, nameof(RequestTimeoutHandler));
                }
            }
        }

        private void SafeInvoke(EventHandler<ReadResponseArgs> handler, ReadResponseArgs response)
        {
            if (handler == null)
                return;

            foreach (EventHandler<ReadResponseArgs> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, response);
                }
                catch (Exception ex)
                {
                    ReportCallbackException(ex, GetResponseCallbackName(handler));
                }
            }
        }

        private string GetResponseCallbackName(EventHandler<ReadResponseArgs> handler)
        {
            if (handler == ReadCoilsResponseHandler) return nameof(ReadCoilsResponseHandler);
            if (handler == ReadDiscreteInputsResponseHandler) return nameof(ReadDiscreteInputsResponseHandler);
            if (handler == ReadHoldingRegistersResponseHandler) return nameof(ReadHoldingRegistersResponseHandler);
            if (handler == ReadInputRegistersResponseHandler) return nameof(ReadInputRegistersResponseHandler);
            if (handler == WriteSingleCoilResponseHandler) return nameof(WriteSingleCoilResponseHandler);
            if (handler == WriteMultipleCoilsResponseHandler) return nameof(WriteMultipleCoilsResponseHandler);
            if (handler == WriteSingleRegisterResponseHandler) return nameof(WriteSingleRegisterResponseHandler);
            if (handler == WriteMultipleRegistersResponseHandler) return nameof(WriteMultipleRegistersResponseHandler);
            return "ResponseHandler";
        }

        private void ProcessPacket(byte[] packet)
        {
            if (packet == null || packet.Length < 5)
                return;

            ushort calculatedCrc = CRC16_MODBUS(packet, packet.Length - 2);
            byte crcLow = (byte)(calculatedCrc & 0xFF);
            byte crcHigh = (byte)(calculatedCrc >> 8);
            if (packet[packet.Length - 2] != crcLow || packet[packet.Length - 1] != crcHigh)
            {
                Interlocked.Increment(ref crcFailCount);
                return;
            }

            ushort startAddress;
            ushort requestedQuantity;
            if (!TryCompleteExpectedResponse(packet, out startAddress, out requestedQuantity))
                return;

            byte function = (byte)(packet[1] & 0x7F);
            var response = new ReadResponseArgs
            {
                CrcOk = true,
                Frame = packet,
                SlaveId = packet[0],
                Function = function,
                StartAddress = startAddress,
                RequestedQuantity = requestedQuantity,
                IsException = (packet[1] & 0x80) != 0
            };
            if (response.IsException)
                response.ExceptionCode = packet[2];
            else
                DecodeReadData(response, function, requestedQuantity);

            switch (function)
            {
                case 0x01:
                    SafeInvoke(ReadCoilsResponseHandler, response);
                    break;
                case 0x02:
                    SafeInvoke(ReadDiscreteInputsResponseHandler, response);
                    break;
                case 0x03:
                    SafeInvoke(ReadHoldingRegistersResponseHandler, response);
                    break;
                case 0x04:
                    SafeInvoke(ReadInputRegistersResponseHandler, response);
                    break;
                case 0x05:
                    SafeInvoke(WriteSingleCoilResponseHandler, response);
                    break;
                case 0x06:
                    SafeInvoke(WriteSingleRegisterResponseHandler, response);
                    break;
                case 0x0F:
                    SafeInvoke(WriteMultipleCoilsResponseHandler, response);
                    break;
                case 0x10:
                    SafeInvoke(WriteMultipleRegistersResponseHandler, response);
                    break;
            }
        }

    }
}
