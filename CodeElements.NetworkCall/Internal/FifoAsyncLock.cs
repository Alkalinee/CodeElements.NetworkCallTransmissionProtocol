﻿using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodeElements.NetworkCall.Internal
{
    //Inspired by https://stackoverflow.com/a/961904/4166138
    public class FifoAsyncLock : IDisposable
    {
        private readonly SemaphoreSlim _lockSemaphore;
        private int _ticketsCount = 0;
        private int _ticketToRide = 1;

        public FifoAsyncLock()
        {
            _lockSemaphore = new SemaphoreSlim(1, 1);
        }

        public void Dispose()
        {
            _lockSemaphore?.Dispose();
        }

        public async Task EnterAsync()
        {
            var ticket = Interlocked.Increment(ref _ticketsCount);
            while (true)
            {
                await _lockSemaphore.WaitAsync();

                if (ticket == _ticketToRide)
                    return;
            }
        }

        public void Release()
        {
            Interlocked.Increment(ref _ticketToRide);
            _lockSemaphore.Release();
        }
    }
}