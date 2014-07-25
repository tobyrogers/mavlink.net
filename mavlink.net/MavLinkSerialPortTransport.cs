using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MavLinkNet;
using System.IO;
using System.IO.Ports;
using System.Collections.Concurrent;
using System.Threading;


namespace MavLinkNet
{
    public class MavLinkSerialPortTransport : MavLinkGenericTransport
    {
        public int HeartBeatUpdateRateMs = 1000;

        private ConcurrentQueue<byte[]> mReceiveQueue = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<UasMessage> mSendQueue = new ConcurrentQueue<UasMessage>();
        private AutoResetEvent mReceiveSignal = new AutoResetEvent(false);
        private AutoResetEvent mSendSignal = new AutoResetEvent(false);
        private MavLinkAsyncWalker mMavLink = new MavLinkAsyncWalker();
        private SerialPort mSerialPort;
        private string mPortName;
        private int mBaudRate;

        public MavLinkSerialPortTransport(string portName, int baudRate)
        {
            mPortName = portName;
            mBaudRate = baudRate;
        }

        public override void Initialize()
        {
            InitializeMavLink();
            InitialiseSerialPort(mPortName, mBaudRate); //9600
        }

        public override void Dispose()
        {
            mSerialPort.Close();
            mReceiveSignal.Set();
            mSendSignal.Set();
        }

        private void InitializeMavLink()
        {
            mMavLink.PacketReceived += HandlePacketReceived;
        }

        private void InitialiseSerialPort(string portName, int baudRate)
        {
//            _serialPort = new System.IO.Ports.SerialPort("/dev/ttys1", 19200, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
//            mSerialPort.Encoding = System.Text.Encoding.Default;
//            _serialPort.WriteTimeout = 1000;
//            _serialPort.DataReceived += OnDataReceived;
//            _serialPort.Open();

            mSerialPort = new SerialPort(portName, baudRate, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
            mSerialPort.Encoding = System.Text.Encoding.Default;
            mSerialPort.DataReceived += DataReceived;
            //mSerialPort.WriteTimeout = 1000;
            mSerialPort.Open();

            Console.WriteLine("BaudRate {0}", mSerialPort.BaudRate);

            ThreadPool.QueueUserWorkItem(new WaitCallback(ProcessReceiveQueue));
        }

        // __ Receive _________________________________________________________


        private void DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            byte[] data = new byte[mSerialPort.BytesToRead];
            mSerialPort.Read(data, 0, data.Length);
            mReceiveQueue.Enqueue(data);
            mReceiveSignal.Set(); // Signal processReceive thread
        }

        private void ProcessReceiveQueue(object state)
        {
            while (true)
            {
                byte[] buffer;

                if (mReceiveQueue.TryDequeue(out buffer))
                {
                    mMavLink.ProcessReceivedBytes(buffer, 0, buffer.Length);
                }
                else
                {
                    mReceiveSignal.WaitOne(); // Empty queue, sleep until signalled
                }
            }

            //  HandleReceptionEnded(this);
        }


        // __ Send ____________________________________________________________


        private void ProcessSendQueue(object state)
        {
            while (true)
            {
                UasMessage msg;

                if (mSendQueue.TryDequeue(out msg))
                {
                    //SendMavlinkMessage(state as IPEndPoint, msg);
                }
                else
                {
                    // Queue is empty, sleep until signalled
                    mSendSignal.WaitOne();
                }
            }
        }

        //private void SendMavlinkMessage(IPEndPoint ep, UasMessage msg)
        //{
        //    byte[] buffer = mMavLink.SerializeMessage(msg, MavlinkSystemId, MavlinkComponentId, true);

        //    mUdpClient.Send(buffer, buffer.Length, ep);
        //}


        // __ Heartbeat _______________________________________________________


        public void BeginHeartBeatLoop()
        {
            ThreadPool.QueueUserWorkItem(new WaitCallback(HeartBeatLoop), null);
        }

        private void HeartBeatLoop(object state)
        {
            while (true)
            {
                foreach (UasMessage m in UavState.GetHeartBeatObjects())
                {
                    SendMessage(m);
                }

                Thread.Sleep(HeartBeatUpdateRateMs);
            }
        }


        // __ API _____________________________________________________________


        public override void SendMessage(UasMessage msg)
        {
            mSendQueue.Enqueue(msg);

            // Signal send thread
            mSendSignal.Set();
        }

    }
}
