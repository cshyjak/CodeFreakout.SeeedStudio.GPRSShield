﻿using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;

namespace CodeFreakout.SeeedStudio.GPRSShield
{
    public class GPRSShield
    {
        private readonly SerialPort _serial;
        private readonly StringBuilder _resultBuffer = new StringBuilder();
        private readonly AutoResetEvent _serialDataFinished = new AutoResetEvent(false);
        private string _lastResult = "";
        private readonly string _apn;
        private int _failures = 0;
        private readonly OutputPort _power;
        
        public GPRSShield(string apn, string portName)
        {
            _apn = apn;

            _power = new OutputPort(Cpu.Pin.GPIO_Pin9, false);

            _serial = new SerialPort(portName, 19200, Parity.None, 8, StopBits.One);
            _serial.Handshake = Handshake.RequestToSend;

            _serial.DataReceived += SerialOnDataReceived;

            _serial.Open();

            Thread.Sleep(10000);
            
            while (_lastResult.IndexOf("OK") < 0)
            {
                Thread.Sleep(1000);

                SendCommand("AT\r", true); //Echo OFF
            }

            InitializeModem();
        }

        public void TogglePower()
        {
            _power.Write(true);
            Thread.Sleep(2500);
            _power.Write(false);
        }

        public string GetInternationMobileEquipmentIdentifier()
        {
            SendCommand("AT+GSN", true);
            return _lastResult;
        }

        private void SerialOnDataReceived(object sender, SerialDataReceivedEventArgs serialDataReceivedEventArgs)
        {
            // Check if Chars are received
            if (serialDataReceivedEventArgs.EventType == SerialData.Chars)
            {
                // Create new buffer
                var readBuffer = new byte[_serial.BytesToRead];

                // Read bytes from buffer
                _serial.Read(readBuffer, 0, readBuffer.Length);

                // Encode to string
                _resultBuffer.Append(new String(Encoding.UTF8.GetChars(readBuffer)));

                if (_resultBuffer.Length > 0 && _resultBuffer[_resultBuffer.Length - 1] == 10)
                {
                    _lastResult = _resultBuffer.ToString();

                    _resultBuffer.Clear();

                    Debug.Print(_lastResult);

                    _serialDataFinished.Set();
                }
            }
        }

        private void InitializeModem()
        {
            SendCommand("ATE0\r", true); //Echo ON

            SendCommand("AT+CIFSR\r", true);

            if (_lastResult.IndexOf("ERROR") >= 0)
            {                
                do
                {
                    SendCommand("AT+CIPSTATUS\r", true);

                    if (_lastResult.IndexOf("IP INITIAL") >= 0)
                    {
                        SendCommand("AT+CSTT=\"" + _apn + "\"\r", true); //Set APN              
                    }
                    else if (_lastResult.IndexOf("IP START") >= 0)
                    {
                        SendCommand("AT+CIICR\r", true);
                    }

                    SendCommand("AT+CIFSR\r", true);
                } while (_lastResult.IndexOf("ERROR") >= 0);
            }
        }

        public void Post(string host, int port, string page, string contentType, string data)
        {
            var connectAttempts = 1;
            var errorOccurred = false;

            _serial.Flush();
            
            SendCommand("AT+CIPSTART=\"TCP\",\"" + host + "\",\"" + port + "\"\r", true);

            Debug.Print(_lastResult);

            while (_lastResult.IndexOf("CONNECT OK") < 0 && _lastResult.IndexOf("ALREADY CONNECT") < 0 && connectAttempts <= 3)
            {
                _serialDataFinished.WaitOne(5000, false);
                connectAttempts++;
            }

            if (_lastResult.IndexOf("CONNECT OK") >= 0 || _lastResult.IndexOf("ALREADY CONNECT") >= 0)
            {
                SendCommand("AT+CIPSTATUS\r", true);
                Thread.Sleep(1000);
                SendCommand("AT+CIPSEND\r", true);

                if (_lastResult.IndexOf("ERROR") > 0)
                {
                    HandleFailure();
                    
                    Post(host, port, page, contentType, data);

                    errorOccurred = true;
                }

                if(!errorOccurred)
                {
                    Thread.Sleep(500);

                    SendCommand("POST " + page + " HTTP/1.1\r\n");
                    SendCommand("Host: " + host + "\r\n");
                    SendCommand("Content-Length: " + data.Length + "\r\n");
                    SendCommand("Content-Type: " + contentType + "\r\n\r\n");
                    SendCommand(data + "\r");

                    _serial.Flush();

                    SendEndOfDataCommand();

                    _serialDataFinished.WaitOne(5000, false);

                    SendCommand("AT+CIPCLOSE\r", true);

                    if (_lastResult.IndexOf("ERROR") > 0)
                    {
                        HandleFailure();

                        Post(host, port, page, contentType, data);
                    }
                    else
                    {
                        _failures = 0;
                    }    
                }
            }
            else
            {
                Debug.Print("Error on open connection.  Re-initializing.");
                
                HandleFailure();

                Post(host, port, page, contentType, data);
            }   
        }

        private void HandleFailure()
        {
            _failures++;

            Debug.Print("Failures: " + _failures);

            if (_failures % 2 == 0)
            {
                SendCommand("AT+CIPSHUT\r", true);

                InitializeModem();    
            }
            else
            {
                SendCommand("AT+CIPCLOSE\r", true);    
            }

            //Sleep for a second allowing events to process to hopefully sync up the serial data communication
            for (int i = 1; i <= 10; i++)
            {
                Thread.Sleep(100);
            }
        }

        public void SendCommand(string command, bool waitForResponse = false, int timeout = 1000)
        {
            _serialDataFinished.Reset();
            _lastResult = "";

            var writeBuffer = Encoding.UTF8.GetBytes(command);

            _serial.Write(writeBuffer, 0, writeBuffer.Length);

            if (waitForResponse)
            {
                _serialDataFinished.WaitOne(timeout, false);
            }
        }

        public void SendEndOfDataCommand()
        {
            _serialDataFinished.Reset();
            _lastResult = "";

            var endChar = new byte[1];
            endChar[0] = 26;
            
            _serial.Write(endChar, 0, 1);

            _serialDataFinished.WaitOne(1000, false);

            Debug.Print(_lastResult);
        }

        public void SMS(string phoneNumber, string message)
        {
            SendCommand("AT+CMGF=1\r", true);
            SendCommand("AT+CMGS=\""+ phoneNumber +"\"\r", true);
            SendCommand(message + "\r");
            SendEndOfDataCommand();
        }
    }
}
