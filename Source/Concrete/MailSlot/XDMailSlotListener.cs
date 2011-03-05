﻿/*=============================================================================
*
*	(C) Copyright 2011, Michael Carlisle (mike.carlisle@thecodeking.co.uk)
*
*   http://www.TheCodeKing.co.uk
*  
*	All rights reserved.
*	The code and information is provided "as-is" without waranty of any kind,
*	either expressed or implied.
*
*=============================================================================
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace TheCodeKing.Net.Messaging.Concrete.MailSlot
{
    /// <summary>
    ///   Implementation of IXDListener. This uses a Mutex to synchronize access
    ///   to the MailSlot for a particular channel, such that only one listener will
    ///   pickup messages on a single machine per channel.
    /// </summary>
    internal sealed class XDMailSlotListener : IXDListener
    {
        #region Constants and Fields

        /// <summary>
        ///   The number of most recent message ids to store.
        /// </summary>
        private const int lastMessageBufferSize = 20;

        /// <summary>
        ///   The unique name of the Mutex used for locking access to the MailSlot for a named
        ///   channel.
        /// </summary>
        private const string mutexNetworkDispatcher = @"Global\XDMailSlotListener";

        /// <summary>
        ///   The base name of the MailSlot on the current machine.
        /// </summary>
        private static readonly string mailSlotIdentifier = string.Concat(@"\\.", XDMailSlotBroadcast.SlotLocation);

        /// <summary>
        ///   A hash table of Thread instances used for reading the MailSlot
        ///   for specific channels.
        /// </summary>
        private readonly Dictionary<string, MailSlotThreadInfo> activeThreads;

        /// <summary>
        ///   Lock object used for synchronizing access to the activeThreads list.
        /// </summary>
        private readonly object lockObj = new object();

        /// <summary>
        ///   Records the last message ids received.
        /// </summary>
        private readonly Queue<Guid> recentMessages = new Queue<Guid>();

        /// <summary>
        ///   Indicates whether the object has been disposed.
        /// </summary>
        private bool disposed;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        ///   The default constructor.
        /// </summary>
        internal XDMailSlotListener()
        {
            activeThreads = new Dictionary<string, MailSlotThreadInfo>(StringComparer.InvariantCultureIgnoreCase);
        }

        /// <summary>
        ///   Deconstructor, cleans unmanaged resources only
        /// </summary>
        ~XDMailSlotListener()
        {
            Dispose(false);
        }

        #endregion

        #region Events

        /// <summary>
        ///   The delegate used to dispatch the MessageReceived event.
        /// </summary>
        public event XDListener.XDMessageHandler MessageReceived;

        #endregion

        #region Implemented Interfaces

        #region IDisposable

        /// <summary>
        ///   Dispose implementation which ensures all resources are destroyed.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        #region IXDListener

        /// <summary>
        ///   Create a new listener thread which will try and obtain the mutex. If it can't
        ///   because another process is already polling this channel then it will wait until 
        ///   it can gain an exclusive lock.
        /// </summary>
        /// <param name = "channelName"></param>
        public void RegisterChannel(string channelName)
        {
            MailSlotThreadInfo channelThread;
            if (!activeThreads.TryGetValue(channelName, out channelThread))
            {
                // only lock if changing
                lock (lockObj)
                {
                    // double check has not been modified before lock
                    if (!activeThreads.TryGetValue(channelName, out channelThread))
                    {
                        channelThread = StartNewThread(channelName);
                        activeThreads[channelName] = channelThread;
                    }
                }
            }
        }

        /// <summary>
        ///   Unregisters the current instance from the given channel. No more messages will be 
        ///   processed, and another process will be allowed to obtain the listener lock.
        /// </summary>
        /// <param name = "channelName"></param>
        public void UnRegisterChannel(string channelName)
        {
            MailSlotThreadInfo info;
            if (activeThreads.TryGetValue(channelName, out info))
            {
                // only lock if changing
                lock (lockObj)
                {
                    // double check has not been modified before lock
                    if (activeThreads.TryGetValue(channelName, out info))
                    {
                        // removing form hash shuts down the thread loop
                        activeThreads.Remove(channelName);
                    }
                }
                if (info != null)
                {
                    // close any read handles
                    if (info.HasValidFileHandle)
                    {
                        Native.CloseHandle(info.FileHandle);
                    }
                    if (info.Thread.IsAlive)
                    {
                        // interrupt incase of asleep thread
                        info.Thread.Interrupt();
                    }
                    if (info.Thread.IsAlive)
                    {
                        // attempt to join thread
                        if (!info.Thread.Join(500))
                        {
                            // if no response within timeout, force abort
                            info.Thread.Abort();
                        }
                    }
                }
            }
        }

        #endregion

        #endregion

        #region Methods

        /// <summary>
        ///   Dispose implementation, which ensures the native window is destroyed
        /// </summary>
        private void Dispose(bool disposeManaged)
        {
            if (!disposed)
            {
                disposed = true;
                if (disposeManaged)
                {
                    if (MessageReceived != null)
                    {
                        // remove all handlers
                        Delegate[] del = MessageReceived.GetInvocationList();
                        foreach (XDListener.XDMessageHandler msg in del)
                        {
                            MessageReceived -= msg;
                        }
                    }
                    if (activeThreads != null)
                    {
                        // grab a reference to the current list of threads
                        var values = new List<MailSlotThreadInfo>(activeThreads.Values);

                        // removing the channels, will cause threads to terminate
                        activeThreads.Clear();
                        // shut down listener threads
                        foreach (var info in values)
                        {
                            // close any read handles
                            if (info.HasValidFileHandle)
                            {
                                Native.CloseHandle(info.FileHandle);
                            }

                            // ensure threads shut down 
                            if (info.Thread.IsAlive)
                            {
                                // interrupt incase of asleep thread
                                info.Thread.Interrupt();
                            }
                            // try to join thread
                            if (info.Thread.IsAlive)
                            {
                                if (!info.Thread.Join(500))
                                {
                                    // last resort abort thread
                                    info.Thread.Abort();
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        ///   The worker thread entry point for polling the MailSlot. Threads will queue until a Mutex becomes
        ///   available for a particular channel. This ensures a system wide singleton for processing network 
        ///   messages.
        /// </summary>
        /// <param name = "state"></param>
        private void MailSlotChecker(object state)
        {
            var info = (MailSlotThreadInfo) state;
            string mutextKey = string.Concat(mutexNetworkDispatcher, ".", info.ChannelName);
            var accessControl = new MutexSecurity();
            var sid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            accessControl.SetAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
            try
            {
                bool isOwner;
                using (var mutex = new Mutex(true, mutextKey, out isOwner, accessControl))
                {
                    // if doesn't own mutex then wait
                    if (!isOwner)
                    {
                        try
                        {
                            mutex.WaitOne();
                            isOwner = true;
                        }
                        catch (ThreadInterruptedException)
                        {
                        }
                            // shut down thread
                        catch (AbandonedMutexException)
                        {
                            // This thread is now the owner
                            isOwner = true;
                        }
                    }

                    if (isOwner)
                    {
                        // enter message read loop
                        ProcessMessages(info);

                        // if this thread owns mutex then release it
                        mutex.ReleaseMutex();
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // unable to obtain mutex, silently give up
            }
        }

        /// <summary>
        ///   This method processes the message and triggers the MessageReceived event.
        /// </summary>
        /// <param name = "dataGram"></param>
        private void OnMessageReceived(DataGram dataGram)
        {
            if (MessageReceived != null)
            {
                MessageReceived(this, new XDMessageEventArgs(dataGram));
            }
        }

        /// <summary>
        ///   Extracts the message for the buffer and raises the MessageReceived event.
        /// </summary>
        /// <param name = "buffer"></param>
        /// <param name = "bytesRead"></param>
        private void ProcessMessage(byte[] buffer, uint bytesRead)
        {
            var b = new BinaryFormatter();
            string rawMessage = string.Empty;
            using (var stream = new MemoryStream())
            {
                stream.Write(buffer, 0, (int) bytesRead);
                stream.Flush();
                // reset the stream cursor back to the beginning
                stream.Seek(0, SeekOrigin.Begin);
                try
                {
                    rawMessage = (string) b.Deserialize(stream);
                }
                catch (SerializationException)
                {
                } // if something goes wrong such as handle is closed,
                // we will not process this message
            }

            MailSlotDataGram dataGram = MailSlotDataGram.ExpandFromRaw(rawMessage);
            // only dispatch event if this is a new message
            // this filters out mailslot duplicates which are sent once per protocol
            if (dataGram.IsValid && !recentMessages.Contains(dataGram.Id))
            {
                // remember we have seen this message
                lock (recentMessages)
                {
                    // double check still not added
                    if (!recentMessages.Contains(dataGram.Id))
                    {
                        recentMessages.Enqueue(dataGram.Id);
                        while (recentMessages.Count > lastMessageBufferSize)
                        {
                            recentMessages.Dequeue();
                        }
                        OnMessageReceived(dataGram);
                    }
                }
            }
        }

        /// <summary>
        ///   This helper method puts the thread into a read message
        ///   loop.
        /// </summary>
        /// <param name = "info"></param>
        private void ProcessMessages(MailSlotThreadInfo info)
        {
            int bytesToRead = 512, maxMessageSize = 0, messageCount = 0, readTimeout = 0;
            // for as long as thread is alive and the channel is registered then act as the MailSlot reader
            while (!disposed && activeThreads.ContainsKey(info.ChannelName))
            {
                // if the channel mailslot is not open try to open it
                if (!info.HasValidFileHandle)
                {
                    info.FileHandle = Native.CreateMailslot(string.Concat(mailSlotIdentifier, info.ChannelName), 0,
                                                            Native.MAILSLOT_WAIT_FOREVER, IntPtr.Zero);
                }

                // if there is a valid read handle try to read messages
                if (info.HasValidFileHandle)
                {
                    var buffer = new byte[bytesToRead];
                    uint bytesRead;
                    // this blocks until a message is received, the message cannot be buffered with overlap structure
                    // so the bytes array must be larger than the current item in order to read the complete message
                    while (Native.ReadFile(info.FileHandle, buffer, (uint) bytesToRead, out bytesRead, IntPtr.Zero))
                    {
                        ProcessMessage(buffer, bytesRead);
                        // reset buffer size
                        bytesToRead = 512;
                        buffer = new byte[bytesToRead];
                    }
                    int code = Marshal.GetLastWin32Error();
                    switch (code)
                    {
                        case Native.ERROR_INSUFFICIENT_BUFFER:
                            // insufficent buffer size, we need to the increase buffer size to read the current item
                            Native.GetMailslotInfo(info.FileHandle, ref maxMessageSize, ref bytesToRead,
                                                   ref messageCount, ref readTimeout);
                            break;
                        case Native.ERROR_INVALID_HANDLE:
                            // close handle if invalid
                            if (info.HasValidFileHandle)
                            {
                                Native.CloseHandle(info.FileHandle);
                                info.FileHandle = IntPtr.Zero;
                            }
                            break;
                        case Native.ERROR_HANDLE_EOF:
                            // read handle has been closed
                            info.FileHandle = IntPtr.Zero;
                            break;
                    }
                }
            }
        }

        /// <summary>
        ///   Helper method starts up a new listener thread for a given channel.
        /// </summary>
        /// <param name = "channelName">The channel name.</param>
        /// <returns></returns>
        private MailSlotThreadInfo StartNewThread(string channelName)
        {
            // create and start the thread at low priority
            var thread = new Thread(MailSlotChecker) {Priority = ThreadPriority.Lowest, IsBackground = true};
            var info = new MailSlotThreadInfo(channelName, thread);
            thread.Start(info);
            return info;
        }

        #endregion
    }
}