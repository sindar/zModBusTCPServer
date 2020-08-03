using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;

namespace zModBusTCPServer
{
    public class ModBusTCPServer
    {
        const uint CLIENT_TIMEOUT = 5000;
        const uint MAX_CONNECTIONS = 10;

        Dictionary<ModBusFunctionCodes, Func<byte[], byte[]>> modBusFunctions;

        Socket server = null;

        ushort[] HoldingRegs;
        ushort[] InputRegs;

        Timer processingTimer;
        uint connectionsCount = 0;

        Queue<WriteQueueItem> writeQueue;

        private class ClientHandler
        {
            public const int BUFFER_SIZE = 256;
            public Socket ClientConnection { get; set; }
            public Timer noActivityTimer;
            public ManualResetEvent queryDone = new ManualResetEvent(false);
            byte[] buffer = new byte[BUFFER_SIZE];
            

            public byte[] Buffer
            {
                get { return buffer; }
                set { buffer = value; }
            }

            public void CloseConnection()
            {
                this.ClientConnection.Shutdown(SocketShutdown.Both);
                this.ClientConnection.Close();
            }
        }

        private class WriteQueueItem
        {
            public ClientHandler clientHandler;
            public byte[] receivedData;

            public WriteQueueItem(ClientHandler clnHandler, byte[] rcvData)
            {
                clientHandler = clnHandler;
                receivedData = rcvData;
            }
        }

        public ModBusTCPServer(ushort[] holdingRegs, ushort[] inputRegs)
        {
            writeQueue = new Queue<WriteQueueItem>();

            // DANGER! TODO: need to find better solution
            HoldingRegs = holdingRegs;
            InputRegs = inputRegs;

            modBusFunctions
                = new Dictionary<ModBusFunctionCodes, Func<byte[], byte[]>>();
            modBusFunctions.Add(ModBusFunctionCodes.ReadHoldingRegs,
                                ReadHoldingAndInputRegs);
            modBusFunctions.Add(ModBusFunctionCodes.ReadInputRegs,
                                ReadHoldingAndInputRegs);
            modBusFunctions.Add(ModBusFunctionCodes.WriteSingleReg,
                                WriteSingleReg);
            modBusFunctions.Add(ModBusFunctionCodes.WriteMultipleHoldingRegs,
                                WriteMultipleHoldingRegs);
        }
        public bool StartServer()
        {
            if (server != null && server.Connected)
                server.Disconnect(false);

            server = new Socket(AddressFamily.InterNetwork, SocketType.Stream,
                                ProtocolType.Tcp);
            EndPoint endPoint = new IPEndPoint(IPAddress.Any, 502);

            try
            {
                server.Bind(endPoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error binding socket!" + ex.Message
                                  + DateTime.Now);
                return false;
            }

            try
            {
                server.Listen(20);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error, cannot start listening!" + ex.Message
                                  + DateTime.Now);
                return false;
            }

            processingTimer = new Timer(ProcessingTimerCallback,
                                        this,
                                        1000,
                                        1000);
            return true;
        }

        //public void MakeRegsCopy(ushort[] holdingRegs, ushort[] inputRegs)
        //{
        //    try
        //    {
        //        Array.Copy(holdingRegs, HoldingRegs, holdingRegs.Length);
        //        Array.Copy(inputRegs, InputRegs, inputRegs.Length);
        //    }
        //    catch(Exception ex)
        //    {
        //        Console.WriteLine("Error copying registers!" + ex.Message);
        //    }
            
        //}

        public void ProcessWriteQueue()
        {
            ClientHandler clientHandler;
            byte[] transmitData;
            byte[] receivedData;
            ModBusFunctionCodes funcCode;

            WriteQueueItem writeQueueItem;
            while (writeQueue.Count > 0)
            {
                writeQueueItem = writeQueue.Dequeue();
                receivedData = writeQueueItem.receivedData;
                clientHandler = writeQueueItem.clientHandler;
                funcCode = (ModBusFunctionCodes)receivedData[7];

                transmitData = modBusFunctions[funcCode](receivedData);

                clientHandler.queryDone.Reset();
                clientHandler.ClientConnection.BeginSend(transmitData, 0,
                                              transmitData.Length,
                                              SocketFlags.None,
                                              new AsyncCallback(SendCallback),
                                              clientHandler);
                clientHandler.queryDone.WaitOne();
                clientHandler.ClientConnection.BeginReceive(clientHandler.Buffer, 0,
                                    ClientHandler.BUFFER_SIZE,
                                    SocketFlags.None,
                                    new AsyncCallback(ReceiveCallback),
                                    clientHandler);
            }
        }

        private void ProcessingTimerCallback(object state)
        {
            ModBusTCPServer thisServer = (ModBusTCPServer)state;
            if (thisServer.connectionsCount < MAX_CONNECTIONS)
            {
                AcceptConnections();
                ++connectionsCount;
            }
        }

        private void AcceptConnections()
        {
            server.BeginAccept(new AsyncCallback(AsyncAcceptCallback), server);
        }

        private void AsyncAcceptCallback(IAsyncResult ar)
        {
            Socket serverSocket = (Socket)ar.AsyncState;

            ClientHandler clientHandler = new ClientHandler();
            clientHandler.ClientConnection = serverSocket.EndAccept(ar);
            Console.WriteLine("Client connected: " + DateTime.Now);

            clientHandler.noActivityTimer = new Timer(NoActivityTimerCallback,
                                                      clientHandler,
                                                      CLIENT_TIMEOUT, 
                                                      Timeout.Infinite);

            clientHandler.ClientConnection.BeginReceive(clientHandler.Buffer, 0,
                                                     ClientHandler.BUFFER_SIZE,
                                                     SocketFlags.None,
                                                     new AsyncCallback(ReceiveCallback),
                                                     clientHandler);
        }

        private void NoActivityTimerCallback(object state)
        {
            ClientHandler clientHandler = (ClientHandler)state;
            Console.WriteLine("No activity from client, closing socket: "
                              + DateTime.Now);
            --connectionsCount;
            clientHandler.CloseConnection();
            clientHandler.noActivityTimer.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            ClientHandler clientHandler = (ClientHandler)ar.AsyncState;
            int bytes;

            try
            {
                bytes = clientHandler.ClientConnection.EndReceive(ar);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Got excpetion:" + ex.Message);
                return;
            }

            if (bytes > 0)
            {
                clientHandler.noActivityTimer.Change(Timeout.Infinite,
                                                     Timeout.Infinite);

                byte[] receivedData = new byte[clientHandler.Buffer.Length];
                byte[] transmitData;

                Array.Copy(clientHandler.Buffer, receivedData,
                           clientHandler.Buffer.Length);
                
                ModBusFunctionCodes funcCode 
                    = (ModBusFunctionCodes)receivedData[7];

                if (modBusFunctions.ContainsKey(funcCode))
                {
                    if (funcCode == ModBusFunctionCodes.WriteSingleReg
                        || funcCode == ModBusFunctionCodes.WriteMultipleHoldingRegs)
                    {
                        if(writeQueue.Count < 16)
                        {
                            writeQueue.Enqueue(new WriteQueueItem(clientHandler,
                                                                  receivedData));
                            transmitData = null;
                        }
                        else
                        {
                            transmitData = ExceptionResponse(receivedData,
                                                     ModBusExceptionCodes.SlaveDeviceBusy);
                        }
                            
                    }
                    else
                    {
                        transmitData = modBusFunctions[funcCode](receivedData);
                    }
                }
                else
                {
                    transmitData = ExceptionResponse(receivedData,
                                                     ModBusExceptionCodes.IllegalFunction);
                }

                if (transmitData != null)
                {
                    clientHandler.queryDone.Reset();
                    clientHandler.ClientConnection.BeginSend(transmitData, 0,
                                                  transmitData.Length,
                                                  SocketFlags.None,
                                                  new AsyncCallback(SendCallback),
                                                  clientHandler);
                    clientHandler.queryDone.WaitOne();
                }

                clientHandler.noActivityTimer.Change(CLIENT_TIMEOUT,
                                                     Timeout.Infinite);

                clientHandler.ClientConnection.BeginReceive(clientHandler.Buffer, 0,
                                            ClientHandler.BUFFER_SIZE,
                                            SocketFlags.None,
                                            new AsyncCallback(ReceiveCallback),
                                            clientHandler);
            }
        }

        private void SendCallback(IAsyncResult ar)
        {
            try
            {
                ClientHandler clientHandler = (ClientHandler)ar.AsyncState;
                int bytesSent = clientHandler.ClientConnection.EndSend(ar);
                clientHandler.queryDone.Set();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }
        
        #region ModBus Functions
        private byte[] ExceptionResponse(byte[] receivedData, 
                                         ModBusExceptionCodes exCode)
        {
            byte[] transmitData = new byte[9];
            //======Preparing Header=========
            for (int i = 0; i < 4; ++i)
                transmitData[i] = receivedData[i];

            transmitData[4] = 0;
            transmitData[5] = 3;

            transmitData[6] = receivedData[6]; //Unit ID
            transmitData[7] = (byte)(receivedData[7] | 0x80); //Function Code
            //======Preparing Header=========

            transmitData[8] = (byte)exCode; //Illegal Function Exception Code

            return transmitData;
        }

        private byte[] ReadHoldingAndInputRegs(byte[] receivedData)
        {
            ushort registersCount = (ushort)((ushort)(receivedData[10] << 8)
                                              | (ushort)receivedData[11]);
            ushort firstRegister = (ushort)((ushort)(receivedData[8] << 8)
                                             | (ushort)receivedData[9]);
            byte[] transmitData = new byte[9 + registersCount * 2];
            ushort remainBytes = (ushort)(3 + registersCount * 2);

            if (((UInt32)firstRegister + (UInt32)registersCount) > 65535)
                return ExceptionResponse(receivedData,
                                         ModBusExceptionCodes.IllegalDataAddress);

            //======Preparing Header=========
            CopyCommonMBHeaderPart(receivedData, transmitData);
            transmitData[4] = (byte)(remainBytes >> 8);
            transmitData[5] = (byte)remainBytes;
            //======Preparing Header=========

            transmitData[8] = (byte)(registersCount * 2); //Bytes count

            //======Data======
            if (receivedData[7] == 0x3)
            {
                CopyRegsForTransmit(HoldingRegs, firstRegister, registersCount,
                                    transmitData);
            } else {
                CopyRegsForTransmit(InputRegs, firstRegister, registersCount,
                                    transmitData);
            }

            return transmitData;
        }

        private void CopyRegsForTransmit(ushort[] regs, int firstRegister,
                                         int registersCount, byte[] transmitData)
        {
            for (int i = firstRegister, j = 0; i < firstRegister + registersCount; ++i)
            {
                transmitData[9 + j++] = (byte)(regs[i] >> 8);
                transmitData[9 + j++] = (byte)regs[i];
            }
        }

        private byte[] WriteSingleReg(byte[] receivedData)
        {
            byte bytesCount = receivedData[12];
            byte[] transmitData = new byte[12];
            ushort registerNumber = (ushort)((ushort)(receivedData[8] << 8)
                                             | (ushort)receivedData[9]);

            HoldingRegs[registerNumber] = (ushort)((ushort)receivedData[10] << 8
                                          | (ushort)receivedData[11]);

            for (int i = 0; i < 12; ++i)
                transmitData[i] = receivedData[i];

            return transmitData;
        }

        private byte[] WriteMultipleHoldingRegs(byte[] receivedData)
        {
            const byte valuesStart = 13;
            ushort firstRegister = (ushort)((ushort)(receivedData[8] << 8)
                                             | (ushort)receivedData[9]);
            ushort registersCount = (ushort)((ushort)(receivedData[10] << 8)
                                  | (ushort)receivedData[11]);
            byte bytesCount = receivedData[12];
            byte[] transmitData = new byte[12];

            //======Preparing Header=========
            CopyCommonMBHeaderPart(receivedData, transmitData);
            transmitData[4] = 0;
            transmitData[5] = 6;
            //======Preparing Header=========

            for (int i = 8; i < 12; ++i)
                transmitData[i] = receivedData[i];

            if (((UInt32)firstRegister + (UInt32)registersCount) > 65535)
                return ExceptionResponse(receivedData, 
                                         ModBusExceptionCodes.IllegalDataAddress);

            if(receivedData.Length < (valuesStart + bytesCount))
                return ExceptionResponse(receivedData, 
                                         ModBusExceptionCodes.IllegalDataValue);
                                         
            ushort j = firstRegister;
            for (byte i = valuesStart; i < valuesStart + bytesCount; i += 2)
            {
                HoldingRegs[j] = (ushort)((ushort)receivedData[i] << 8
                                          | (ushort)receivedData[i + 1]);
                ++j;
            }

            return transmitData;
        }

        private void CopyCommonMBHeaderPart(byte[] receivedData, byte[] transmitData)
        {
            //Transaction and Protocol ID's
            for (int i = 0; i < 4; ++i)
                transmitData[i] = receivedData[i];

            transmitData[6] = receivedData[6]; //Unit ID
            transmitData[7] = receivedData[7]; //Function Code
        }
        #endregion

    }
}
