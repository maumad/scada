﻿/*
 * Copyright 2015 Mikhail Shiryaev
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 * 
 * 
 * Product  : Rapid SCADA
 * Module   : ScadaCommCommon
 * Summary  : UDP connection with a device
 * 
 * Author   : Mikhail Shiryaev
 * Created  : 2015
 * Modified : 2015
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Scada.Comm.Channels
{
    /// <summary>
    /// UDP connection with a device
    /// <para>UDP-соединение с КП</para>
    /// </summary>
    /// <remarks>In fact, UDP does not create a connection object
    /// <para>На самом деле протокол UDP не создаёт объект соединения</para></remarks>
    public class UdpConnection : Connection
    {
        /// <summary>
        /// Таймаут приёма датаграммы, мс
        /// </summary>
        protected const int DatagramReceiveTimeout = 10;

        /// <summary>
        /// Буфер неполностью считанной датаграммы
        /// </summary>
        protected byte[] datagramBuf;
        /// <summary>
        /// Позиция чтения из буфера датаграммы
        /// </summary>
        protected int bufReadPos;

        
        /// <summary>
        /// Конструктор, ограничивающий создание объекта без параметров
        /// </summary>
        protected UdpConnection()
        {
        }
        
        /// <summary>
        /// Конструктор
        /// </summary>
        public UdpConnection(UdpClient udpClient, int localPort, int remotePort)
            : base()
        {
            if (udpClient == null)
                throw new ArgumentNullException("udpClient");

            datagramBuf = null;
            bufReadPos = 0;

            UdpClient = udpClient;
            UdpClient.Client.SendTimeout = DefaultWriteTimeout;
            LocalPort = localPort;
            RemotePort = remotePort;
        }


        /// <summary>
        /// Получить UDP-клиента
        /// </summary>
        public UdpClient UdpClient { get; protected set; }

        /// <summary>
        /// Получить или установить локальный порт
        /// </summary>
        public int LocalPort { get; set; }

        /// <summary>
        /// Получить или установить удалённый порт
        /// </summary>
        public int RemotePort { get; set; }


        /// <summary>
        /// Создать объект IPEndPoint, описывающий удалённый хост
        /// </summary>
        protected IPEndPoint CreateIPEndPoint()
        {
            return new IPEndPoint(IPAddress.Parse(RemoteAddress), RemotePort);
        }

        /// <summary>
        /// Принять датаграмму по UPD
        /// </summary>
        protected byte[] ReceiveDatagram(ref IPEndPoint endPoint, out int readPos, out bool isNew)
        {
            if (datagramBuf == null)
            {
                // приём новой датаграммы
                readPos = 0;
                isNew = true;
                try { return UdpClient.Receive(ref endPoint); }
                catch (SocketException) { return null; }
            }
            else
            {
                // возврат неполностью считанной датаграммы
                byte[] datagram = datagramBuf;
                readPos = bufReadPos;
                isNew = false;

                datagramBuf = null;
                bufReadPos = 0;

                return datagram;
            }
        }

        /// <summary>
        /// Сохранить неполностью считанную датаграмму
        /// </summary>
        protected void StoreDatagram(byte[] datagram, int readPos)
        {
            if (datagram != null && readPos < datagram.Length)
            {
                datagramBuf = datagram;
                bufReadPos = readPos;
            }
        }


        /// <summary>
        /// Считать данные
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count, int timeout, 
            CommUtils.ProtocolLogFormats logFormat, out string logText)
        {
            try
            {
                int readCnt = 0;
                DateTime nowDT = DateTime.Now;
                DateTime startDT = nowDT;
                DateTime stopDT = startDT.AddMilliseconds(timeout);
                IPEndPoint endPoint = CreateIPEndPoint();
                UdpClient.Client.ReceiveTimeout = DatagramReceiveTimeout;

                while (readCnt < count && startDT <= nowDT && nowDT <= stopDT)
                {
                    // считывание данных
                    int readPos;
                    bool isNew;
                    byte[] datagram = ReceiveDatagram(ref endPoint, out readPos, out isNew);

                    // копирование полученных данных в заданный буфер
                    if (datagram != null && datagram.Length > 0)
                    {
                        int requiredCnt = count - readCnt; // осталось считать
                        int copyCnt = Math.Min(datagram.Length - readPos, requiredCnt);
                        Array.Copy(datagram, readPos, buffer, readCnt + offset, copyCnt);
                        readCnt += copyCnt;
                        readPos += copyCnt;
                    }

                    // накопление данных во внутреннем буфере соединения
                    if (readCnt < count && isNew)
                        Thread.Sleep(DataAccumThreadDelay);

                    StoreDatagram(datagram, readPos);
                    nowDT = DateTime.Now;
                }

                logText = BuildReadLogText(buffer, offset, count, readCnt, logFormat);
                return readCnt;
            }
            catch (SocketException ex)
            {
                logText = CommPhrases.ReadDataError + ": " + ex.Message;
                return 0;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(CommPhrases.ReadDataError + ": " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Считать данные с условиями остановки чтения
        /// </summary>
        public override int Read(byte[] buffer, int offset, int maxCount, int timeout, BinStopCondition stopCond, 
            out bool stopReceived, CommUtils.ProtocolLogFormats logFormat, out string logText)
        {
            try
            {
                int readCnt = 0;
                DateTime nowDT = DateTime.Now;
                DateTime startDT = nowDT;
                DateTime stopDT = startDT.AddMilliseconds(timeout);
                IPEndPoint endPoint = CreateIPEndPoint();

                stopReceived = false;
                byte stopCode = stopCond.StopCode;
                UdpClient.Client.ReceiveTimeout = DatagramReceiveTimeout;

                while (readCnt < maxCount && !stopReceived && startDT <= nowDT && nowDT <= stopDT)
                {
                    // считывание данных
                    int readPos;
                    bool isNew;
                    byte[] datagram = ReceiveDatagram(ref endPoint, out readPos, out isNew);

                    if (datagram != null && datagram.Length > 0)
                    {
                        // поиск кода остановки в считанных данных
                        int datagramLen = datagram.Length;
                        int stopCodeInd = -1;
                        for (int i = readPos; i < datagramLen && !stopReceived; i++)
                        {
                            if (datagram[i] == stopCode)
                            {
                                stopCodeInd = i;
                                stopReceived = true;
                            }
                        }

                        // копирование полученных данных в заданный буфер
                        int requiredCnt = stopReceived ? stopCodeInd - readCnt + 1 : maxCount - readCnt;
                        int copyCnt = Math.Min(datagram.Length - readPos, requiredCnt);
                        Array.Copy(datagram, readPos, buffer, readCnt + offset, copyCnt);
                        readCnt += copyCnt;
                        readPos += copyCnt;
                    }

                    // накопление данных во внутреннем буфере соединения
                    if (readCnt < maxCount && !stopReceived && isNew)
                        Thread.Sleep(DataAccumThreadDelay);

                    StoreDatagram(datagram, readPos);
                    nowDT = DateTime.Now;
                }

                logText = BuildReadLogText(buffer, offset, readCnt, logFormat);
                return readCnt;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(CommPhrases.ReadDataWithStopCondError + ": " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Считать строки
        /// </summary>
        public override List<string> ReadLines(int timeout, TextStopCondition stopCond, 
            out bool stopReceived, out string logText)
        {
            try
            {
                List<string> lines = new List<string>();
                stopReceived = false;

                DateTime nowDT = DateTime.Now;
                DateTime startDT = nowDT;
                DateTime stopDT = startDT.AddMilliseconds(timeout);
                IPEndPoint endPoint = CreateIPEndPoint();
                UdpClient.Client.ReceiveTimeout = DatagramReceiveTimeout;

                while (!stopReceived && startDT <= nowDT && nowDT <= stopDT)
                {
                    // считывание данных
                    int readPos;
                    bool isNew;
                    byte[] datagram = ReceiveDatagram(ref endPoint, out readPos, out isNew);

                    if (datagram != null && datagram.Length > 0)
                    {
                        // получение строк из считанных данных
                        int datagramLen = datagram.Length;
                        StringBuilder sbLine = new StringBuilder(datagramLen);

                        while (readPos < datagramLen && !stopReceived)
                        {
                            sbLine.Append(Encoding.Default.GetChars(datagram, readPos, 1));
                            readPos++;
                            bool newLineFound = StringBuilderEndsWith(sbLine, NewLine);
                            if (newLineFound || readPos == datagramLen)
                            {
                                string line = newLineFound ?
                                    sbLine.ToString(0, sbLine.Length - NewLine.Length) :
                                    sbLine.ToString();
                                lines.Add(line);
                                sbLine.Clear();
                                stopReceived = stopCond.CheckCondition(lines, line);
                            }
                        }
                    }

                    // накопление данных во внутреннем буфере соединения
                    if (!stopReceived && isNew)
                        Thread.Sleep(DataAccumThreadDelay);

                    StoreDatagram(datagram, readPos);
                    nowDT = DateTime.Now;
                }

                logText = BuildReadLinesLogText(lines);
                return lines;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(CommPhrases.ReadLinesError + ": " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Записать данные
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count, 
            CommUtils.ProtocolLogFormats logFormat, out string logText)
        {
            try
            {
                if (string.IsNullOrEmpty(RemoteAddress))
                    throw new InvalidOperationException("RemoteAddress is undefined.");
                if (RemotePort <= 0)
                    throw new InvalidOperationException("RemotePort is undefined.");

                byte[] datagram;
                if (offset > 0)
                {
                    datagram = new byte[count];
                    Array.Copy(buffer, offset, datagram, 0, count);
                }
                else
                {
                    datagram = buffer;
                }

                try
                {
                    int sentCnt = UdpClient.Send(datagram, count, RemoteAddress, RemotePort);
                    logText = BuildWriteLogText(datagram, 0, count, logFormat);
                }
                catch (SocketException ex)
                {
                    logText = CommPhrases.WriteDataError + ": " + ex.Message;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(CommPhrases.WriteDataError + ": " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Записать строку
        /// </summary>
        public override void WriteLine(string text, out string logText)
        {
            try
            {
                byte[] buffer = Encoding.Default.GetBytes(text + NewLine);
                Write(buffer, 0, buffer.Length, CommUtils.ProtocolLogFormats.String, out logText);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(CommPhrases.WriteLineError + ": " + ex.Message, ex);
            }
        }


        /// <summary>
        /// Закрыть соединение
        /// </summary>
        public void Close()
        {
            try { UdpClient.Close(); }
            catch { }
        }

        /// <summary>
        /// Очистить буфер неполностью считанной датаграммы
        /// </summary>
        public void ClearDatagramBuffer()
        {
            datagramBuf = null;
            bufReadPos = 0;
        }
    }
}
