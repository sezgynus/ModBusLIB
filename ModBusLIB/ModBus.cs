using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ModBusLIB
{
    
    public class ModBus
    {   
        public SerialPort Port { get; private set; }
        public int CrcFailCount { get; private set; }
        private long t1_5;
        private long t3_5;
        private Stopwatch microtimer = new Stopwatch();
        private byte[] rx_buf, tx_buf;
        private int rx_buf_index = 0;
        private bool us_timer_flag = false;
        private bool new_packet=false;
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

        public sealed class RequestTimeoutArgs : EventArgs
        {
            public byte slave_id { get; set; }
            public byte function { get; set; }
            public int retries { get; set; }
        }

        private void SendRequest(byte slaveId, byte function, int packetSize)
        {
            lock (request_lock)
            {
                if (Port == null || !Port.IsOpen)
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
                    if (Port != null && Port.IsOpen)
                        Port.Write(tx_buf, 0, packetSize);
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
            public byte[] pdu { get; set; }
            public bool crc_ok { get; set; }
            public byte slave_id { get; set; }
            public bool ex_resp { get; set; }
            public byte ex_code { get; set; }
            public bool[] bits { get; set; }
            public ushort[] registers { get; set; }
        }

        private static void DecodeReadData(ReadResponseArgs response, byte function)
        {
            if (response.ex_resp || response.pdu == null || response.pdu.Length < 5)
                return;

            int byteCount = response.pdu[2];
            if (function == 0x01 || function == 0x02)
            {
                response.bits = new bool[byteCount * 8];
                for (int i = 0; i < response.bits.Length; i++)
                    response.bits[i] = (response.pdu[3 + (i / 8)] & (1 << (i % 8))) != 0;
            }
            else if (function == 0x03 || function == 0x04)
            {
                response.registers = new ushort[byteCount / 2];
                for (int i = 0; i < response.registers.Length; i++)
                    response.registers[i] = (ushort)((response.pdu[3 + i * 2] << 8) | response.pdu[4 + i * 2]);
            }
        }
        public void Close()
        {
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

            if (Port != null && Port.IsOpen)
                Port.Close();

            microtimer.Stop();
            microtimer.Reset();
            us_timer = null;
        }
        public void Initialize(string portName, int baudRate=115200, Parity parity=Parity.Even)
        {
            if (us_timer_flag || (Port != null && Port.IsOpen))
                throw new InvalidOperationException("ModBus is already initialized. Call Close() before initializing again.");

            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("A serial port name is required.", nameof(portName));
            if (baudRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(baudRate));

            StopBits stopBits;
            if (parity == Parity.None) stopBits = StopBits.Two;
            else stopBits = StopBits.One;
            if (baudRate > 19200)
            {
                t3_5 = 1750;
                t1_5 = 750;
            }
            else {
                t1_5 = 16500000 / baudRate;
                t3_5 = 38500000 / baudRate;
            }
            Port = new SerialPort(portName, baudRate, parity, 8, stopBits);

            Port.DataReceived += new SerialDataReceivedEventHandler(serial_rx);
            Port.Open();

            rx_buf = new byte[4096];
            tx_buf = new byte[8];

            us_timer_flag = true;
            us_timer = new Thread(new ThreadStart(us_timer_task));
            us_timer.Start();
            microtimer.Start();
        }
        public void ReadCoils(byte slave_id, ushort start, ushort count)//0x01
        {
            ValidateRequest(slave_id, start, count, 1, 2000);
            modbus_read_serializer(0x01, slave_id, start, count);
            SendRequest(slave_id, 0x01, 8);
        }
        public void ReadDiscreteInputs(byte slave_id, ushort start, ushort count)//0x02
        {
            ValidateRequest(slave_id, start, count, 1, 2000);
            modbus_read_serializer(0x02, slave_id, start, count);
            SendRequest(slave_id, 0x02, 8);
        }
        public void ReadHoldingRegisters(byte slave_id, ushort start, ushort count)//0x03
        {
            ValidateRequest(slave_id, start, count, 1, 125);
            modbus_read_serializer(0x03, slave_id, start, count);
            SendRequest(slave_id, 0x03, 8);
        }
        public void ReadInputRegisters(byte slave_id, ushort start, ushort count)//0x04
        {
            ValidateRequest(slave_id, start, count, 1, 125);
            modbus_read_serializer(0x04, slave_id, start, count);
            SendRequest(slave_id, 0x04, 8);
        }
        public void WriteSingleCoil(byte slave_id, ushort adress, bool coil_value)//0x05
        {
            ValidateSlaveId(slave_id);
            byte[] value = new byte[2];
            int packet_size;
            if (coil_value)
            {
                value[0] = 0xFF;
                value[1] = 0x00;
            }
            else
            {
                value[0] = 0x00;
                value[1] = 0x00;
            }
            packet_size=modbus_write_serializer(0x05, slave_id, adress, 0, value);
            SendRequest(slave_id, 0x05, packet_size);
        }
        public int WriteMultipleCoils(byte slave_id, ushort start, ushort count,byte[] pdata)//0x15
        {
            ValidateRequest(slave_id, start, count, 1, 1968);
            int requiredBytes = (count + 7) / 8;
            if (pdata == null || pdata.Length < requiredBytes)
                throw new ArgumentException("Packed coil data is shorter than the requested quantity.", nameof(pdata));
            int packet_size;
            packet_size=modbus_write_serializer(0x0F, slave_id, start, count, pdata);
            SendRequest(slave_id, 0x0F, packet_size);
            return packet_size;
        }
        public int WriteSingleRegister(byte slave_id, ushort adress, ushort udata)//0x06
        {
            ValidateSlaveId(slave_id);
            int packet_size;
            ushort[] udat = { udata };
            packet_size = modbus_write_serializer(0x06, slave_id, adress, 0, null ,udat);
            SendRequest(slave_id, 0x06, packet_size);
            return packet_size;
        }
        public int WriteMultipleRegisters(byte slave_id, ushort start, ushort count, ushort[] udata)//0x16
        {
            ValidateRequest(slave_id, start, count, 1, 123);
            if (udata == null || udata.Length < count)
                throw new ArgumentException("Register data is shorter than the requested quantity.", nameof(udata));
            int packet_size;
            packet_size = modbus_write_serializer(0x10, slave_id, start, count, null, udata);
            SendRequest(slave_id, 0x10, packet_size);
            return packet_size;
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

        private int modbus_read_serializer(byte function, byte slave_id, ushort start, ushort count)
        {
            int l = 8;
            tx_buf = new byte[l];
            if ((function == 0x01) | (function == 0x02) | (function == 0x03) | (function == 0x04))
            {
                tx_buf[0] = slave_id;
                tx_buf[1] = function;

                tx_buf[2] = BitConverter.GetBytes(start)[1];
                tx_buf[3] = BitConverter.GetBytes(start)[0];

                tx_buf[4] = BitConverter.GetBytes(count)[1];
                tx_buf[5] = BitConverter.GetBytes(count)[0];

                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);

                tx_buf[6] = BitConverter.GetBytes(calculated_crc)[0];
                tx_buf[7] = BitConverter.GetBytes(calculated_crc)[1];
                l = 8;
            }
            return l;
        }
        private int modbus_write_serializer(byte function, byte slave_id, ushort start, ushort count, byte[] bdata=null, ushort[] udata=null)
        {
            int l = 0;
            int frameLength = (function == 0x0F) ? 9 + ((count + 7) / 8)
                            : (function == 0x10) ? 9 + (count * 2)
                            : 8;
            tx_buf = new byte[frameLength];
            if (function == 0x05)
            {
                l = 8;
                tx_buf[0] = slave_id;
                tx_buf[1] = function;

                tx_buf[2] = BitConverter.GetBytes(start)[1];
                tx_buf[3] = BitConverter.GetBytes(start)[0];

                tx_buf[4] = bdata[0];
                tx_buf[5] = bdata[1];
                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);
                tx_buf[l - 2] = BitConverter.GetBytes(calculated_crc)[0];
                tx_buf[l - 1] = BitConverter.GetBytes(calculated_crc)[1];
                
            }
            else if (function == 0x0F)
            {
                tx_buf[0] = slave_id;
                tx_buf[1] = function;

                tx_buf[2] = BitConverter.GetBytes(start)[1];
                tx_buf[3] = BitConverter.GetBytes(start)[0];

                tx_buf[4] = BitConverter.GetBytes(count)[1];
                tx_buf[5] = BitConverter.GetBytes(count)[0];
                if ((count % 8) == 0) tx_buf[6] = (byte)(count / 8);
                else tx_buf[6] = (byte)((count / 8) + 1);
                l = 7;
                for (int i = 0; i < tx_buf[6]; i++)
                {
                    tx_buf[i + 7] = bdata[i];
                    l++;
                }
                l += 2;
                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);
                tx_buf[l - 2] = BitConverter.GetBytes(calculated_crc)[0];
                tx_buf[l - 1] = BitConverter.GetBytes(calculated_crc)[1];
            }
            if (function == 0x06)
            {
                l = 8;
                tx_buf[0] = slave_id;
                tx_buf[1] = function;

                tx_buf[2] = BitConverter.GetBytes(start)[1];
                tx_buf[3] = BitConverter.GetBytes(start)[0];

                tx_buf[4] = BitConverter.GetBytes(udata[0])[1];
                tx_buf[5] = BitConverter.GetBytes(udata[0])[0];
                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);
                tx_buf[l - 2] = BitConverter.GetBytes(calculated_crc)[0];
                tx_buf[l - 1] = BitConverter.GetBytes(calculated_crc)[1];

            }
            if (function == 0x10)
            {
                l = 8;
                tx_buf[0] = slave_id;
                tx_buf[1] = function;

                tx_buf[2] = BitConverter.GetBytes(start)[1];
                tx_buf[3] = BitConverter.GetBytes(start)[0];

                tx_buf[4] = BitConverter.GetBytes(count)[1];
                tx_buf[5] = BitConverter.GetBytes(count)[0];
                tx_buf[6] = (byte)(count * 2);
                l = 7;
                for (int i = 0; i < count; i++)
                {
                    tx_buf[l] = BitConverter.GetBytes(udata[i])[1];
                    tx_buf[l + 1] = BitConverter.GetBytes(udata[i])[0];
                    l+=2;
                }
                l += 2;
                ushort calculated_crc = CRC16_MODBUS(tx_buf, l - 2);
                tx_buf[l - 2] = BitConverter.GetBytes(calculated_crc)[0];
                tx_buf[l - 1] = BitConverter.GetBytes(calculated_crc)[1];

            }
            return l;
        }
        private void us_timer_task()
        {
            while (us_timer_flag)
            {
                CheckRequestTimeout();
                modbus_timer_Tick();
                Thread.Sleep(1);
            }
        }

        private bool IsExpectedResponse(byte[] packet)
        {
            lock (request_lock)
            {
                if (!request_pending || packet == null || packet.Length < 5)
                    return false;
                if (packet[0] != pending_slave_id || (packet[1] & 0x7F) != pending_function)
                    return false;

                bool exception = (packet[1] & 0x80) != 0;
                if (exception)
                    return packet.Length == 5;

                if (pending_function >= 0x01 && pending_function <= 0x04)
                {
                    ushort requestedCount = (ushort)((pending_frame[4] << 8) | pending_frame[5]);
                    int expectedBytes = (pending_function == 0x01 || pending_function == 0x02)
                        ? (requestedCount + 7) / 8
                        : requestedCount * 2;
                    return packet[2] == expectedBytes && packet.Length == expectedBytes + 5;
                }

                if (pending_function == 0x05 || pending_function == 0x06 ||
                    pending_function == 0x0F || pending_function == 0x10)
                {
                    if (packet.Length != 8)
                        return false;
                    for (int i = 2; i <= 5; i++)
                        if (packet[i] != pending_frame[i])
                            return false;
                    return true;
                }
                return false;
            }
        }

        private void CheckRequestTimeout()
        {
            RequestTimeoutArgs timeout = null;
            byte[] retryFrame = null;

            lock (request_lock)
            {
                if (!request_pending || ResponseTimeoutMs <= 0)
                    return;

                if ((microtimer.ElapsedMilliseconds - pending_since_ms) < ResponseTimeoutMs)
                    return;

                if (pending_retry_count < MaxRetries)
                {
                    retryFrame = pending_frame;
                    pending_retry_count++;
                    pending_since_ms = microtimer.ElapsedMilliseconds;
                }
                else
                {
                    timeout = new RequestTimeoutArgs
                    {
                        slave_id = pending_slave_id,
                        function = pending_function,
                        retries = pending_retry_count
                    };
                    request_pending = false;
                    pending_frame = null;
                }
            }

            if (retryFrame != null)
            {
                try
                {
                    if (Port != null && Port.IsOpen)
                        Port.Write(retryFrame, 0, retryFrame.Length);
                }
                catch (InvalidOperationException)
                {
                    // Keep the worker alive. The request will expire normally.
                }
                catch (System.IO.IOException)
                {
                    // Serial transport failures are handled by the normal timeout path.
                }
                catch (UnauthorizedAccessException)
                {
                    // The port may have disappeared or become unavailable between checks.
                }
                return;
            }

            if (timeout != null)
                RequestTimeoutHandler?.Invoke(this, timeout);
        }

        private void serial_rx(object sender, SerialDataReceivedEventArgs e)
        {
            lock (rx_lock)
            {
            new_packet = true;
            int length = Port.BytesToRead;
            for (int i = 0; i < length; i++)
            {
                if (rx_buf_index >= rx_buf.Length)
                {
                    rx_buf_index = 0;
                    new_packet = false;
                    Port.DiscardInBuffer();
                    break;
                }
                if (Port != null && Port.IsOpen) rx_buf[rx_buf_index] = (byte)Port.ReadByte();
                rx_buf_index++;
                last_rx_us = (long)(((double)microtimer.ElapsedTicks / Stopwatch.Frequency) * 1000000);
            }
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

        private void ProcessPacket(byte[] packet)
        {
            if (packet == null || packet.Length < 5)
                return;

            ushort calculatedCrc = CRC16_MODBUS(packet, packet.Length - 2);
            byte crcLow = (byte)(calculatedCrc & 0xFF);
            byte crcHigh = (byte)(calculatedCrc >> 8);
            if (packet[packet.Length - 2] != crcLow || packet[packet.Length - 1] != crcHigh)
            {
                CrcFailCount++;
                return;
            }

            if (!IsExpectedResponse(packet))
                return;

            lock (request_lock)
            {
                if (request_pending && packet[0] == pending_slave_id &&
                    (packet[1] & 0x7F) == pending_function)
                {
                    request_pending = false;
                    pending_frame = null;
                }
            }

            byte function = (byte)(packet[1] & 0x7F);
            var response = new ReadResponseArgs
            {
                crc_ok = true,
                pdu = packet,
                slave_id = packet[0],
                ex_resp = (packet[1] & 0x80) != 0
            };
            if (response.ex_resp)
                response.ex_code = packet[2];
            else
                DecodeReadData(response, function);

            switch (function)
            {
                case 0x01:
                    ReadCoilsResponseHandler?.Invoke(this, response);
                    break;
                case 0x02:
                    ReadDiscreteInputsResponseHandler?.Invoke(this, response);
                    break;
                case 0x03:
                    ReadHoldingRegistersResponseHandler?.Invoke(this, response);
                    break;
                case 0x04:
                    ReadInputRegistersResponseHandler?.Invoke(this, response);
                    break;
                case 0x05:
                    WriteSingleCoilResponseHandler?.Invoke(this, response);
                    break;
                case 0x06:
                    WriteSingleRegisterResponseHandler?.Invoke(this, response);
                    break;
                case 0x0F:
                    WriteMultipleCoilsResponseHandler?.Invoke(this, response);
                    break;
                case 0x10:
                    WriteMultipleRegistersResponseHandler?.Invoke(this, response);
                    break;
            }
        }

    }
}
