﻿using System;
using System.Threading.Tasks;
using CodeElements.NetworkCallTransmission.Exceptions;
using CodeElements.NetworkCallTransmission.Internal;
using ZeroFormatter;

namespace CodeElements.NetworkCallTransmission
{
    /// <summary>
    ///     The server side of the network protocol. The counterpart is <see cref="CallTransmission{TInterface}" />
    /// </summary>
    /// <typeparam name="TInterface">The remote interface. The receiving site must have the same interface available.</typeparam>
    public class CallTransmissionExecuter<TInterface>
    {
        private const int EstimatedResultBufferSize = 1000;
        private readonly TInterface _interfaceImplementation;

        /// <summary>
        ///     Initialize a new instance of <see cref="CallTransmissionExecuter{TInterface}" />
        /// </summary>
        /// <param name="interfaceImplementation">The interface which can be called by the remote side</param>
        public CallTransmissionExecuter(TInterface interfaceImplementation)
            : this(interfaceImplementation, ExecuterInterfaceCache.Build<TInterface>())
        {
        }

        /// <summary>
        ///     Initialize a new instance of <see cref="CallTransmission{TInterface}" /> with a cache
        /// </summary>
        /// <param name="interfaceImplementation">The interface which can be called by the remote side</param>
        /// <param name="cache">Contains thread-safe information about the interface methods</param>
        public CallTransmissionExecuter(TInterface interfaceImplementation, ExecuterInterfaceCache cache)
        {
            _interfaceImplementation = interfaceImplementation;
            Cache = cache;
        }

        /// <summary>
        ///     Contains thread-safe information about the interface methods. Please reuse this object when possible to minimize
        ///     the initialization time.
        /// </summary>
        public ExecuterInterfaceCache Cache { get; }

        /// <summary>
        ///     Reserve bytes at the beginning of the response buffer for custom headers
        /// </summary>
        public int CustomOffset { get; set; }

        /// <summary>
        ///     Called when data was received by the client side
        /// </summary>
        /// <param name="buffer">The array of unsigned bytes which contains the information to execute the method.</param>
        /// <param name="offset">The index into buffer at which the data begins</param>
        /// <returns>Returns the answer which should be sent back to the client</returns>
        public async Task<ArraySegment<byte>> ReceiveData(byte[] buffer, int offset)
        {
            //PROTOCOL
            //CALL:
            //HEAD      - 4 bytes                   - Identifier, ASCII (NTC1)
            //HEAD      - integer                   - callback identifier
            //HEAD      - uinteger                  - The method identifier
            //HEAD      - integer * parameters      - the length of each parameter
            //--------------------------------------------------------------------------
            //DATA      - length of the parameters  - serialized parameters
            //
            //RETURN:
            //HEAD      - 4 bytes                   - Identifier, ASCII (NTR1)
            //HEAD      - integer                   - callback identifier
            //HEAD      - 1 byte                    - the response type (0 = executed, 1 = result returned, 2 = exception, 3 = not implemented)
            //(BODY     - return object length      - the serialized return object)

            if (buffer[offset++] != CallProtocolInfo.Header1 || buffer[offset++] != CallProtocolInfo.Header2 ||
                buffer[offset++] != CallProtocolInfo.Header3Call)
                throw new ArgumentException("Invalid package format. Invalid header.");

            if (buffer[offset++] != 1)
                throw new NotSupportedException($"The version {buffer[offset - 1]} is not supported.");

            var id = BitConverter.ToUInt32(buffer, offset + 4);

            void WriteResponseHeader(byte[] data)
            {
                data[CustomOffset] = CallProtocolInfo.Header1;
                data[CustomOffset + 1] = CallProtocolInfo.Header2;
                data[CustomOffset + 2] = CallProtocolInfo.Header3Return;
                data[CustomOffset + 3] = CallProtocolInfo.Header4;
                Buffer.BlockCopy(buffer, offset, data, CustomOffset + 4, 4); //copy callback id
            }

            //method not found/implemented
            if (!Cache.MethodInvokers.TryGetValue(id, out var methodInvoker))
            {
                var response = new byte[CustomOffset /* user offset */ + 8 /* Header */ + 1 /* response type */];
                WriteResponseHeader(response);
                response[CustomOffset + 8] = (byte) CallTransmissionResponseType.MethodNotImplemented;
                return new ArraySegment<byte>(response);
            }

            var parameters = new object[methodInvoker.ParameterCount];
            var parameterOffset = offset + 8 + parameters.Length * 4;

            for (var i = 0; i < methodInvoker.ParameterCount; i++)
            {
                var type = methodInvoker.ParameterTypes[i];
                var parameterLength = BitConverter.ToInt32(buffer, offset + 8 + i * 4);

                parameters[i] = ZeroFormatterSerializer.NonGeneric.Deserialize(type, buffer, parameterOffset);
                parameterOffset += parameterLength;
            }

            Task task;
            try
            {
                task = methodInvoker.Invoke(_interfaceImplementation, parameters);
                await task.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var exceptionInfo = ExceptionFactory.PackException(e);
                var response = new byte[CustomOffset /* user offset */ + 8 /* Header */ + 1 /* response type */ +
                                        EstimatedResultBufferSize /* exception */];
                var length = ZeroFormatterSerializer.Serialize(ref response, CustomOffset + 9, exceptionInfo);

                WriteResponseHeader(response);
                response[CustomOffset + 8] = (byte) CallTransmissionResponseType.Exception;
                return new ArraySegment<byte>(response, 0, length + 9);
            }

            if (methodInvoker.ReturnsResult)
            {
                var result = methodInvoker.TaskReturnPropertyInfo.GetValue(task);

                var response = new byte[CustomOffset + EstimatedResultBufferSize];
                WriteResponseHeader(response);
                response[CustomOffset + 8] = (byte) CallTransmissionResponseType.ResultReturned;

                var responseLength =
                    ZeroFormatterSerializer.NonGeneric.Serialize(methodInvoker.ReturnType, ref response,
                        CustomOffset + 9, result);
                return new ArraySegment<byte>(response, 0, responseLength + CustomOffset + 9);
            }
            else
            {
                var response = new byte[CustomOffset /* user offset */ + 8 /* Header */ + 1 /* response type */];
                WriteResponseHeader(response);
                response[CustomOffset + 8] = (byte) CallTransmissionResponseType.MethodExecuted;
                return new ArraySegment<byte>(response);
            }
        }
    }
}