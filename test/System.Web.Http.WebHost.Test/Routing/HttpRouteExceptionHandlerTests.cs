﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http.ExceptionHandling;
using System.Web.Http.Results;
using Microsoft.TestCommon;
using Moq;

namespace System.Web.Http.WebHost.Routing
{
    public class HttpRouteExceptionHandlerTests
    {
        [Fact]
        public void ExceptionInfo_ReturnsSpecifiedInstance()
        {
            // Arrange
            ExceptionDispatchInfo expectedExceptionInfo = CreateExceptionInfo();
            IExceptionLogger logger = CreateDummyLogger();
            IExceptionHandler handler = CreateDummyHandler();
            HttpRouteExceptionHandler product = CreateProductUnderTest(expectedExceptionInfo, logger, handler);

            // Act
            ExceptionDispatchInfo exceptionInfo = product.ExceptionInfo;

            // Assert
            Assert.Same(exceptionInfo, expectedExceptionInfo);
        }

        [Fact]
        public void ExceptionLogger_ReturnsSpecifiedInstance()
        {
            // Arrange
            ExceptionDispatchInfo exceptionInfo = CreateExceptionInfo();
            IExceptionLogger expectedLogger = CreateDummyLogger();
            IExceptionHandler handler = CreateDummyHandler();
            HttpRouteExceptionHandler product = CreateProductUnderTest(exceptionInfo, expectedLogger, handler);

            // Act
            IExceptionLogger logger = product.ExceptionLogger;

            // Assert
            Assert.Same(expectedLogger, logger);
        }

        [Fact]
        public void ExceptionHandler_ReturnsSpecifiedInstance()
        {
            // Arrange
            ExceptionDispatchInfo exceptionInfo = CreateExceptionInfo();
            IExceptionLogger logger = CreateDummyLogger();
            IExceptionHandler expectedHandler = CreateDummyHandler();
            HttpRouteExceptionHandler product = CreateProductUnderTest(exceptionInfo, logger, expectedHandler);

            // Act
            IExceptionHandler handler = product.ExceptionHandler;

            // Assert
            Assert.Same(expectedHandler, handler);
        }

        [Fact]
        public void ExceptionInfo_IfUsingExceptionInfoConstructor_ReturnsSpecifiedInstance()
        {
            // Arrange
            ExceptionDispatchInfo expectedExceptionInfo = CreateExceptionInfo();
            HttpRouteExceptionHandler product = CreateProductUnderTest(expectedExceptionInfo);

            // Act
            ExceptionDispatchInfo exceptionInfo = product.ExceptionInfo;

            // Assert
            Assert.Same(exceptionInfo, expectedExceptionInfo);
        }

        [Fact]
        public void ExceptionLogger_IfUsingExceptionInfoConstructor_ReturnsExceptionServicesInstance()
        {
            // Arrange
            ExceptionDispatchInfo exceptionInfo = CreateExceptionInfo();
            HttpRouteExceptionHandler product = CreateProductUnderTest(exceptionInfo);

            // Act
            IExceptionLogger logger = product.ExceptionLogger;

            // Assert
            IExceptionLogger expectedLogger = ExceptionServices.GetLogger(GlobalConfiguration.Configuration);
            Assert.Same(expectedLogger, logger);
        }

        [Fact]
        public void ExceptionHandler_IfUsingExceptionInfoConstructor_ReturnsExceptionServicesInstance()
        {
            // Arrange
            ExceptionDispatchInfo exceptionInfo = CreateExceptionInfo();
            HttpRouteExceptionHandler product = CreateProductUnderTest(exceptionInfo);

            // Act
            IExceptionHandler handler = product.ExceptionHandler;

            // Assert
            IExceptionHandler expectedHandler = ExceptionServices.GetHandler(GlobalConfiguration.Configuration);
            Assert.Same(expectedHandler, handler);
        }

        [Fact]
        public void ProcessRequestAsync_IfExceptionIsHttpResponseException_UsesExceptionResponse()
        {
            // Arrange
            HttpStatusCode expectedStatusCode = HttpStatusCode.Forbidden;

            using (HttpResponseMessage response = CreateResponse(expectedStatusCode))
            using (HttpRequestMessage request = CreateRequest())
            {
                ExceptionDispatchInfo exceptionInfo = CreateExceptionInfo(new HttpResponseException(response));
                IExceptionLogger logger = CreateDummyLogger();
                IExceptionHandler handler = CreateDummyHandler();

                HttpRouteExceptionHandler product = CreateProductUnderTest(exceptionInfo, logger, handler);

                int statusCode = 0;
                Mock<HttpResponseBase> responseBaseMock = new Mock<HttpResponseBase>();
                responseBaseMock.SetupSet(r => r.StatusCode = It.IsAny<int>()).Callback<int>((s) => statusCode = s);
                responseBaseMock.SetupGet(r => r.Cache).Returns(() => new Mock<HttpCachePolicyBase>().Object);
                HttpResponseBase responseBase = responseBaseMock.Object;
                HttpContextBase contextBase = CreateStubContextBase(responseBase);
                contextBase.SetHttpRequestMessage(request);

                // Act
                Task task = product.ProcessRequestAsync(contextBase);

                // Assert
                Assert.NotNull(task);
                task.WaitUntilCompleted();
                Assert.Equal(TaskStatus.RanToCompletion, task.Status);

                Assert.Equal((int)expectedStatusCode, statusCode);
            }
        }

        [Fact]
        public void ProcessRequestAsync_IfExceptionIsNotHttpResponseException_CallsExceptionServices()
        {
            // Arrange
            Exception expectedException = CreateException();
            ExceptionDispatchInfo exceptionInfo = CreateExceptionInfo(expectedException);

            Mock<IExceptionLogger> loggerMock = CreateStubExceptionLoggerMock();
            IExceptionLogger logger = loggerMock.Object;
            Mock<IExceptionHandler> handlerMock = CreateStubExceptionHandlerMock();
            IExceptionHandler handler = handlerMock.Object;

            HttpRouteExceptionHandler product = CreateProductUnderTest(exceptionInfo, logger, handler);

            HttpResponseBase responseBase = CreateStubResponseBase();
            HttpContextBase contextBase = CreateStubContextBase(responseBase);

            using (HttpRequestMessage expectedRequest = new HttpRequestMessage())
            {
                contextBase.SetHttpRequestMessage(expectedRequest);

                // Act
                Task task = product.ProcessRequestAsync(contextBase);

                // Assert
                Assert.NotNull(task);
                task.WaitUntilCompleted();
                Assert.Equal(TaskStatus.Faulted, task.Status);

                Func<ExceptionContext, bool> exceptionContextMatches = (c) =>
                    c != null
                    && c.Exception == expectedException
                    && c.CatchBlock == WebHostExceptionCatchBlocks.HttpWebRoute
                    && c.Request == expectedRequest;

                loggerMock.Verify(l => l.LogAsync(It.Is<ExceptionLoggerContext>(c =>
                    c.CanBeHandled == true && exceptionContextMatches(c.ExceptionContext)),
                    CancellationToken.None), Times.Once());
                handlerMock.Verify(l => l.HandleAsync(It.Is<ExceptionHandlerContext>(c =>
                    exceptionContextMatches(c.ExceptionContext)), CancellationToken.None), Times.Once());
            }
        }

        [Fact]
        public void ProcessRequestAsync_IfHandlerHandles_UsesHandlerResult()
        {
            // Arrange
            ExceptionDispatchInfo exceptionInfo = CreateExceptionInfo();

            IExceptionLogger logger = CreateStubExceptionLogger();

            HttpStatusCode expectedStatusCode = HttpStatusCode.Ambiguous;
            Mock<IExceptionHandler> handlerMock = new Mock<IExceptionHandler>(MockBehavior.Strict);
            handlerMock
                .Setup(h => h.HandleAsync(It.IsAny<ExceptionHandlerContext>(), It.IsAny<CancellationToken>()))
                .Returns<ExceptionHandlerContext, CancellationToken>((c, i) =>
                {
                    c.Result = new StatusCodeResult(expectedStatusCode, new HttpRequestMessage());
                    return Task.FromResult(0);
                });
            IExceptionHandler handler = handlerMock.Object;

            HttpRouteExceptionHandler product = CreateProductUnderTest(exceptionInfo, logger, handler);

            int statusCode = 0;
            Mock<HttpResponseBase> responseBaseMock = new Mock<HttpResponseBase>();
            responseBaseMock.SetupSet(r => r.StatusCode = It.IsAny<int>()).Callback<int>((c) => statusCode = c);
            responseBaseMock.SetupGet(r => r.Cache).Returns(() => new Mock<HttpCachePolicyBase>().Object);
            HttpResponseBase responseBase = responseBaseMock.Object;
            HttpContextBase contextBase = CreateStubContextBase(responseBase);
            using (HttpRequestMessage request = CreateRequest())
            {
                contextBase.SetHttpRequestMessage(request);

                // Act
                Task task = product.ProcessRequestAsync(contextBase);

                // Assert
                Assert.NotNull(task);
                task.WaitUntilCompleted();
                Assert.Equal(TaskStatus.RanToCompletion, task.Status);

                Assert.Equal((int)expectedStatusCode, statusCode);
            }
        }

        [Fact]
        public void ProcessRequestAsync_IfHandlerDoesNotHandle_ReturnsTaskPropagatingException()
        {
            // Arrange
            Exception expectedException = CreateExceptionWithCallStack();
            string expectedStackTrace = expectedException.StackTrace;
            ExceptionDispatchInfo exceptionInfo = CreateExceptionInfo(expectedException);

            IExceptionLogger logger = CreateStubExceptionLogger();
            IExceptionHandler handler = CreateStubExceptionHandler();

            HttpRouteExceptionHandler product = CreateProductUnderTest(exceptionInfo, logger, handler);

            HttpResponseBase responseBase = CreateStubResponseBase();
            HttpContextBase contextBase = CreateStubContextBase(responseBase);

            using (HttpRequestMessage request = CreateRequest())
            {
                contextBase.SetHttpRequestMessage(request);

                // Act
                Task task = product.ProcessRequestAsync(contextBase);

                // Assert
                Assert.NotNull(task);
                task.WaitUntilCompleted();
                Assert.Equal(TaskStatus.Faulted, task.Status);
                Assert.NotNull(task.Exception); // Guard
                Exception exception = task.Exception.GetBaseException();
                Assert.Same(expectedException, exception);
                Assert.NotNull(exception.StackTrace);
                Assert.True(exception.StackTrace.StartsWith(expectedStackTrace));
            }

        }

        [Fact]
        public void ProcessRequestAsync_IfCopyToAsyncOnErrorResponseThrows_CallsExceptionLogger()
        {
            // Arrange
            Exception expectedOriginalException = CreateException();
            ExceptionDispatchInfo exceptionInfo = CreateExceptionInfo(expectedOriginalException);

            Mock<IExceptionLogger> loggerMock = CreateStubExceptionLoggerMock();
            IExceptionLogger logger = loggerMock.Object;

            Exception expectedErrorException = CreateException();

            using (HttpResponseMessage expectedErrorResponse = new HttpResponseMessage())
            using (HttpRequestMessage expectedRequest = new HttpRequestMessage())
            {
                expectedErrorResponse.Content = CreateFaultingContent(expectedErrorException);

                Mock<IExceptionHandler> handlerMock = new Mock<IExceptionHandler>(MockBehavior.Strict);
                handlerMock
                    .Setup(h => h.HandleAsync(It.IsAny<ExceptionHandlerContext>(), It.IsAny<CancellationToken>()))
                    .Returns<ExceptionHandlerContext, CancellationToken>((c, i) =>
                    {
                        c.Result = new ResponseMessageResult(expectedErrorResponse);
                        return Task.FromResult(0);
                    });
                IExceptionHandler handler = handlerMock.Object;

                HttpRouteExceptionHandler product = CreateProductUnderTest(exceptionInfo, logger, handler);

                HttpResponseBase responseBase = CreateStubResponseBase(Stream.Null);
                HttpContextBase contextBase = CreateStubContextBase(responseBase);
                contextBase.SetHttpRequestMessage(expectedRequest);

                // Act
                Task task = product.ProcessRequestAsync(contextBase);

                // Assert
                Assert.NotNull(task);
                task.WaitUntilCompleted();
                Assert.Equal(TaskStatus.RanToCompletion, task.Status);

                loggerMock.Verify(l => l.LogAsync(It.Is<ExceptionLoggerContext>(c =>
                    c.CanBeHandled == true
                    && c.ExceptionContext != null
                    && c.ExceptionContext.Exception == expectedOriginalException
                    && c.ExceptionContext.CatchBlock == WebHostExceptionCatchBlocks.HttpWebRoute
                    && c.ExceptionContext.Request == expectedRequest),
                    CancellationToken.None), Times.Once());
                loggerMock.Verify(l => l.LogAsync(It.Is<ExceptionLoggerContext>(c =>
                    c.CanBeHandled == false
                    && c.ExceptionContext != null
                    && c.ExceptionContext.Exception == expectedErrorException
                    && c.ExceptionContext.CatchBlock == WebHostExceptionCatchBlocks.HttpControllerHandlerBufferError
                    && c.ExceptionContext.Request == expectedRequest
                    && c.ExceptionContext.Response == expectedErrorResponse),
                    CancellationToken.None), Times.Once());
            }
        }

        private static IExceptionHandler CreateDummyHandler()
        {
            return new Mock<IExceptionHandler>(MockBehavior.Strict).Object;
        }

        private static IExceptionLogger CreateDummyLogger()
        {
            return new Mock<IExceptionLogger>(MockBehavior.Strict).Object;
        }

        private static Exception CreateException()
        {
            return new Exception();
        }

        private static Exception CreateExceptionWithCallStack()
        {
            try
            {
                throw CreateException();
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static ExceptionDispatchInfo CreateExceptionInfo()
        {
            return CreateExceptionInfo(CreateException());
        }

        private static ExceptionDispatchInfo CreateExceptionInfo(Exception exception)
        {
            return ExceptionDispatchInfo.Capture(exception);
        }

        private static HttpContent CreateFaultingContent(Exception exception)
        {
            return new FaultingHttpContent(exception);
        }

        private static HttpRouteExceptionHandler CreateProductUnderTest(ExceptionDispatchInfo exceptionInfo)
        {
            return new HttpRouteExceptionHandler(exceptionInfo);
        }

        private static HttpRouteExceptionHandler CreateProductUnderTest(ExceptionDispatchInfo exceptionInfo,
            IExceptionLogger exceptionLogger, IExceptionHandler exceptionHandler)
        {
            return new HttpRouteExceptionHandler(exceptionInfo, exceptionLogger, exceptionHandler);
        }

        private static HttpRequestMessage CreateRequest()
        {
            return new HttpRequestMessage();
        }

        private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode)
        {
            return new HttpResponseMessage(statusCode);
        }

        private static HttpContextBase CreateStubContextBase(HttpResponseBase response)
        {
            Mock<HttpContextBase> mock = new Mock<HttpContextBase>();
            mock.SetupGet(m => m.Response).Returns(response);
            IDictionary items = new Dictionary<object, object>();
            mock.SetupGet(m => m.Items).Returns(items);
            return mock.Object;
        }

        private static IExceptionHandler CreateStubExceptionHandler()
        {
            return CreateStubExceptionHandlerMock().Object;
        }

        private static Mock<IExceptionHandler> CreateStubExceptionHandlerMock()
        {
            Mock<IExceptionHandler> mock = new Mock<IExceptionHandler>(MockBehavior.Strict);
            mock
                .Setup(h => h.HandleAsync(It.IsAny<ExceptionHandlerContext>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(0));
            return mock;
        }

        private static IExceptionLogger CreateStubExceptionLogger()
        {
            return CreateStubExceptionLoggerMock().Object;
        }

        private static Mock<IExceptionLogger> CreateStubExceptionLoggerMock()
        {
            Mock<IExceptionLogger> mock = new Mock<IExceptionLogger>(MockBehavior.Strict);
            mock
                .Setup(l => l.LogAsync(It.IsAny<ExceptionLoggerContext>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(0));
            return mock;
        }

        private static HttpResponseBase CreateStubResponseBase()
        {
            return new Mock<HttpResponseBase>().Object;
        }

        private static HttpResponseBase CreateStubResponseBase(Stream outputStream)
        {
            Mock<HttpResponseBase> mock = new Mock<HttpResponseBase>();
            mock.Setup(r => r.OutputStream).Returns(outputStream);
            return mock.Object;
        }

        private class FaultingHttpContent : HttpContent
        {
            private readonly Exception _exception;

            public FaultingHttpContent(Exception exception)
            {
                Contract.Assert(exception != null);
                _exception = exception;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                return CreateFaultedTask(_exception);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            private static Task CreateFaultedTask(Exception exception)
            {
                TaskCompletionSource<object> source = new TaskCompletionSource<object>();
                source.SetException(exception);
                return source.Task;
            }
        }
    }
}