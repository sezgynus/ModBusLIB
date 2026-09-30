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
        public SerialPort Port;
        public int crc_fail_count = 0;
        public long t1_5, t3_5,char_time;
        public byte[] Coils = new byte[250];
        public byte[] DiscreteInputs = new byte[250];
        public byte[] InputRegisters = new byte[4000];
        public byte[] HoldingRegisters = new byte[4000];
        private Stopwatch microtimer = new Stopwatch();
        private byte[] rx_buf, tx_buf;
        private int rx_buf_index = 0;
        private bool us_timer_flag = false;
        private bool new_packet=false;
        private long last_rx_us;
        private Thread us_timer;

        public event EventHandler<ReadResponseArgs> ReadCoilsResponseHandler;
        public event EventHandler<ReadResponseArgs> ReadDiscreteInputsResponseHandler;
        public event EventHandler<ReadResponseArgs> ReadHoldingRegistersResponseHandler;
        public event EventHandler<ReadResponseArgs> ReadInputRegistersResponseHandler;
        public event EventHandler<ReadResponseArgs> WriteSingleCoilResponseHandler;
        public event EventHandler<ReadResponseArgs> WriteMultipleCoilsResponseHandler;
        public event EventHandler<ReadResponseArgs> WriteSingleRegisterResponseHandler;
        public event EventHandler<ReadResponseArgs> WriteMultipleRegistersResponseHandler;
        public class ReadResponseArgs : EventArgs
        {
            public byte[] pdu { get; set; }
            public bool crc_ok { get; set; }
            public byte slave_id { get; set; }
            public bool ex_resp { get; set; }
            public byte ex_code { get; set; }
        }
        public void wirtetest(string data)
        {
            Port.WriteLine(data);
        }
        public void Close()
        {
            microtimer.Stop();
            microtimer.Reset();
            new_packet = false;
            if(us_timer != null) us_timer.Abort();
            //rx_buf_index = 0;
            if (us_timer != null) Port.Close();
            us_timer_flag = false;
        }
        public void Initialize(string portName, int baudRate=115200, Parity parity=Parity.Even)
        {
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
            modbus_read_serializer(0x01, slave_id, start, count);
            if (Port.IsOpen & (Port != null)) Port.Write(tx_buf, 0, 8);
        }
        public void ReadDiscreteInputs(byte slave_id, ushort start, ushort count)//0x02
        {
            modbus_read_serializer(0x02, slave_id, start, count);
            if (Port.IsOpen & (Port != null)) Port.Write(tx_buf, 0, 8);
        }
        public void ReadHoldingRegisters(byte slave_id, ushort start, ushort count)//0x03
        {
            modbus_read_serializer(0x03, slave_id, start, count);
            if (Port.IsOpen & (Port != null)) Port.Write(tx_buf, 0, 8);
        }
        public void ReadInputRegisters(byte slave_id, ushort start, ushort count)//0x04
        {
            modbus_read_serializer(0x04, slave_id, start, count);
            if (Port.IsOpen & (Port != null)) Port.Write(tx_buf, 0, 8);
        }
        public void WriteSingleCoil(byte slave_id, ushort adress, bool coil_value)//0x05
        {
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
            if (Port.IsOpen & (Port != null)) Port.Write(tx_buf, 0, packet_size);
        }
        public int WriteMultipleCoils(byte slave_id, ushort start, ushort count,byte[] pdata)//0x15
        {
            int packet_size;
            packet_size=modbus_write_serializer(0x0F, slave_id, start, count, pdata);
            if (Port.IsOpen & (Port != null)) Port.Write(tx_buf, 0, packet_size);
            return packet_size;
        }
        public int WriteSingleRegister(byte slave_id, ushort adress, ushort udata)//0x06
        {
            int packet_size;
            ushort[] udat = { udata };
            packet_size = modbus_write_serializer(0x06, slave_id, adress, 0, null ,udat);
            if (Port.IsOpen & (Port != null)) Port.Write(tx_buf, 0, packet_size);
            return packet_size;
        }
        public int WriteMultipleRegisters(byte slave_id, ushort start, ushort count, ushort[] udata)//0x16
        {
            int packet_size;
            packet_size = modbus_write_serializer(0x10, slave_id, start, count, null, udata);
            if (Port.IsOpen & (Port != null)) Port.Write(tx_buf, 0, packet_size);
            return packet_size;
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
                modbus_timer_Tick();
                Thread.Sleep(1);
            }
        }

        private void serial_rx(object sender, SerialDataReceivedEventArgs e)
        {
            new_packet = true;
            int length = Port.BytesToRead;
            for (int i = 0; i < length; i++)
            {
                if (Port.IsOpen & (Port != null)) rx_buf[rx_buf_index] = (byte)Port.ReadByte();
                rx_buf_index++;
                last_rx_us = (long)(((double)microtimer.ElapsedTicks / Stopwatch.Frequency) * 1000000);
            }
        }

        private void modbus_timer_Tick()
        {
            if (new_packet)
            {
                long nowUs = (long)(((double)microtimer.ElapsedTicks / Stopwatch.Frequency) * 1000000);
                if ((nowUs - last_rx_us) >= t3_5)
                {
                    if (rx_buf_index >= 5)
                    {
                        byte[] packet = new byte[rx_buf_index];
                        for (int i = 0; i < packet.Length; i++)
                        {
                            packet[i] = rx_buf[i];
                        }
                        rx_buf_index = 0;
                        new_packet = false;
                        if (packet.Length > 0)
                        {
                            bool crc_okk = false;
                            ushort calculated_crc = CRC16_MODBUS(packet, (packet.Length - 2));
                            byte[] calc_crc = BitConverter.GetBytes(calculated_crc);
                            byte[] in_crc = new byte[2];
                            in_crc[0] = packet[packet.Length - 2];
                            in_crc[1] = packet[packet.Length - 1];
                            if ((calc_crc[0] == in_crc[0]) &(calc_crc[1] == in_crc[1]))
                            {
                                crc_okk = true;
                            }
                            else
                            {
                                crc_okk = false;
                                crc_fail_count++;
                            }

                            if ((packet[1] & 0x7F) == 0x01)
                            {
                                ReadResponseArgs e = new ReadResponseArgs();
                                e.crc_ok = crc_okk;
                                e.pdu = packet;
                                e.slave_id = packet[0];
                                if ((packet[1] & 0x80) > 0)
                                {
                                    e.ex_code = packet[2];
                                    e.ex_resp = true;
                                }
                                else e.ex_resp = false;
                                ReadCoilsResponseHandler?.Invoke(this, e);
                            }
                            else if ((packet[1] & 0x7F) == 0x02)
                            {
                                ReadResponseArgs e = new ReadResponseArgs();
                                e.crc_ok = crc_okk;
                                e.pdu = packet;
                                e.slave_id = packet[0];
                                if ((rx_buf[1] & 0x80) > 0)
                                {
                                    e.ex_code = packet[2];
                                    e.ex_resp = true;
                                }
                                else e.ex_resp = false;
                                ReadDiscreteInputsResponseHandler?.Invoke(this, e);
                            }
                            else if ((packet[1] & 0x7F) == 0x03)
                            {
                                ReadResponseArgs e = new ReadResponseArgs();
                                e.crc_ok = crc_okk;
                                e.pdu = packet;
                                e.slave_id = packet[0];
                                if ((packet[1] & 0x80) > 0)
                                {
                                    e.ex_code = packet[2];
                                    e.ex_resp = true;
                                }
                                else e.ex_resp = false;
                                ReadHoldingRegistersResponseHandler?.Invoke(this, e);
                            }
                            else if ((packet[1] & 0x7F) == 0x04)
                            {
                                ReadResponseArgs e = new ReadResponseArgs();
                                e.crc_ok = crc_okk;
                                e.pdu = packet;
                                e.slave_id = packet[0];
                                if ((packet[1] & 0x80) > 0)
                                {
                                    e.ex_code = packet[2];
                                    e.ex_resp = true;
                                }
                                else e.ex_resp = false;
                                ReadInputRegistersResponseHandler?.Invoke(this, e);
                            }
                            else if ((packet[1] & 0x7F) == 0x05)
                            {
                                ReadResponseArgs e = new ReadResponseArgs();
                                e.crc_ok = crc_okk;
                                e.pdu = packet;
                                e.slave_id = packet[0];
                                if ((packet[1] & 0x80) > 0)
                                {
                                    e.ex_code = packet[2];
                                    e.ex_resp = true;
                                }
                                else e.ex_resp = false;
                                WriteSingleCoilResponseHandler?.Invoke(this, e);
                            }
                            else if ((packet[1] & 0x7F) == 0x0F)
                            {
                                ReadResponseArgs e = new ReadResponseArgs();
                                e.crc_ok = crc_okk;
                                e.pdu = packet;
                                e.slave_id = packet[0];
                                if ((packet[1] & 0x80) > 0)
                                {
                                    e.ex_code = packet[2];
                                    e.ex_resp = true;
                                }
                                else e.ex_resp = false;
                                WriteMultipleCoilsResponseHandler?.Invoke(this, e);
                            }
                            else if ((packet[1] & 0x7F) == 0x06)
                            {
                                ReadResponseArgs e = new ReadResponseArgs();
                                e.crc_ok = crc_okk;
                                e.pdu = packet;
                                e.slave_id = packet[0];
                                if ((packet[1] & 0x80) > 0)
                                {
                                    e.ex_code = packet[2];
                                    e.ex_resp = true;
                                }
                                else e.ex_resp = false;
                                WriteSingleRegisterResponseHandler?.Invoke(this, e);
                            }
                            else if ((packet[1] & 0x7F) == 0x10)
                            {
                                ReadResponseArgs e = new ReadResponseArgs();
                                e.crc_ok = crc_okk;
                                e.pdu = packet;
                                e.slave_id = packet[0];
                                if ((packet[1] & 0x80) > 0)
                                {
                                    e.ex_code = packet[2];
                                    e.ex_resp = true;
                                }
                                else e.ex_resp = false;
                                WriteMultipleRegistersResponseHandler?.Invoke(this, e);
                            }
                            new_packet = false;
                        }
                    }
                    else
                    {
                        rx_buf_index = 0;
                        new_packet = false;
                    }
                }
            }
        }

    }
}
