using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MavLinkNet;
using System.ComponentModel;


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
        private int mFd;
        private EventHandlerList mEventList;
        object mDataReceived;

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

        static bool IsWindows 
        {
            get 
            {
                PlatformID id = Environment.OSVersion.Platform;
                return id == PlatformID.Win32Windows || id == PlatformID.Win32NT; // WinCE not supported
            }
        }

        private void InitialiseSerialPort(string portName, int baudRate)
        {       
            mSerialPort = new SerialPort(portName, baudRate);
            mSerialPort.DataReceived += DataReceived; // this does not work on mono
            mSerialPort.Open();

            if (!IsWindows)
            {
                FieldInfo fieldInfo = mSerialPort.BaseStream.GetType().GetField("fd", BindingFlags.Instance | BindingFlags.NonPublic);
                mFd = (int)fieldInfo.GetValue(mSerialPort.BaseStream);
                fieldInfo = typeof(SerialPort).GetField("data_received", BindingFlags.Instance | BindingFlags.NonPublic);
                mDataReceived = fieldInfo.GetValue(mSerialPort);
                PropertyInfo propertyInfo = typeof(SerialPort).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic);
                mEventList = (EventHandlerList)propertyInfo.GetValue(mSerialPort,null);
                new System.Threading.Thread(new System.Threading.ThreadStart(this.EventThreadFunction)).Start();
            }               

            ThreadPool.QueueUserWorkItem(new WaitCallback(ProcessReceiveQueue));
        }

        [DllImport ("libc")]
        static extern IntPtr strerror (int errnum);

        [DllImport ("MonoPosixHelper", SetLastError = true)]
        static extern bool poll_serial (int fd, out int error, int timeout);

        private bool Poll()
        {
            int error;
            bool poll_result = poll_serial (mFd, out error, mSerialPort.ReadTimeout);
            if (error == -1) {
                int errnum = Marshal.GetLastWin32Error ();
                string error_message = Marshal.PtrToStringAnsi (strerror (errnum));
                throw new IOException (error_message);
            }
            return poll_result;
        }

        private void EventThreadFunction( )
        {
            do
            {
                if (Poll())
                {
                    SerialDataReceivedEventHandler handler = (SerialDataReceivedEventHandler) mEventList[mDataReceived];
                    if (handler != null) {
                        handler (this, null);
                    }
                }
            }
            while (mSerialPort.IsOpen);
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
