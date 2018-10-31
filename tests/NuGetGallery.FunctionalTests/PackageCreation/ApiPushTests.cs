﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Packaging;
using Xunit;
using Xunit.Abstractions;

namespace NuGetGallery.FunctionalTests.PackageCreation
{
    public class ApiPushTests : GalleryTestBase
    {
        private const int TaskCount = 16;
        private readonly ClientSdkHelper _clientSdkHelper;

        public ApiPushTests(ITestOutputHelper testOutputHelper) : base(testOutputHelper)
        {
            _clientSdkHelper = new ClientSdkHelper(TestOutputHelper);
        }

        [Fact]
        [Description("Pushes many packages of the same ID and version. Verifies exactly one push succeeds and the rest fail with a conflict.")]
        [Priority(2)]
        [Category("P2Tests")]
        public void DuplicatePushesAreRejectedAndNotDeleted()
        {
            // Arrange
            var packageId = $"{nameof(DuplicatePushesAreRejectedAndNotDeleted)}.{DateTime.UtcNow.Ticks}";

            // Hold the test output in memory since we are running the code below in parallel and TestOutputHelper
            // is not thread safe and then synchronously write to `TestOutputHelper`
            int pushVersionCount = 10;
            List<InMemoryTestOutputHelper> testOutputHelpers = new List<InMemoryTestOutputHelper>();
            for (var i = 0; i < pushVersionCount; i++)
            {
                testOutputHelpers.Add(InMemoryTestOutputHelper.New);
            }

            Parallel.For(0, pushVersionCount, async (i) =>
            {
                using (var client = new HttpClient())
                {
                    var inMemoryOutputHelper = testOutputHelpers[i];
                    var packageCreationHelper = new PackageCreationHelper(inMemoryOutputHelper);
                    if (i > 0)
                    {
                        inMemoryOutputHelper.WriteLine(string.Empty);
                        inMemoryOutputHelper.WriteLine(new string('=', 80));
                        inMemoryOutputHelper.WriteLine(string.Empty);
                    }

                    var packageVersion = $"1.0.{i}";
                    inMemoryOutputHelper.WriteLine($"Starting package {packageId} {packageVersion}...");

                    var packagePath = await packageCreationHelper.CreatePackage(packageId, packageVersion);

                    var tasks = new List<Task<HttpStatusCode>>();
                    var barrier = new Barrier(TaskCount);

                    // Act
                    for (var taskIndex = 0; taskIndex < TaskCount; taskIndex++)
                    {
                        tasks.Add(PushAsync(client, packagePath, barrier));
                    }

                    var statusCodes = await Task.WhenAll(tasks);

                    // Assert
                    for (var taskIndex = 1; taskIndex <= statusCodes.Length; taskIndex++)
                    {
                        inMemoryOutputHelper.WriteLine($"Task {taskIndex:D2} push:     HTTP {(int)statusCodes[taskIndex - 1]}");
                    }

                    //Wait for the packages to be available in V2(due to async validation)
                   await _clientSdkHelper.VerifyPackageExistsInV2Async(packageId, packageVersion);

                    var downloadUrl = $"{UrlHelper.V2FeedRootUrl}package/{packageId}/{packageVersion}";
                    using (var response = await client.GetAsync(downloadUrl))
                    {
                        inMemoryOutputHelper.WriteLine($"Package download: HTTP {(int)response.StatusCode}");

                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                        var actualPackageBytes = await response.Content.ReadAsByteArrayAsync();
                        using (var stream = new MemoryStream(actualPackageBytes))
                        using (var packageReader = new PackageArchiveReader(stream))
                        {
                            Assert.Equal(packageId, packageReader.NuspecReader.GetId());
                            Assert.Equal(packageVersion, packageReader.NuspecReader.GetVersion().ToNormalizedString());
                        }
                    }

                    Assert.Equal(1, statusCodes.Count(x => x == HttpStatusCode.Created));
                    Assert.Equal(TaskCount - 1, statusCodes.Count(x => x == HttpStatusCode.Conflict));
                }
            });

            foreach(var testOutputHelper in testOutputHelpers)
            {
                TestOutputHelper.WriteLine(testOutputHelper.GetOutput());
            }
        }

        private async Task<HttpStatusCode> PushAsync(
            HttpClient client,
            string packagePath,
            Barrier barrier)
        {
            using (var package = File.OpenRead(packagePath))
            using (var request = new HttpRequestMessage(HttpMethod.Put, UrlHelper.V2FeedPushSourceUrl))
            {
                request.Content = new StreamContent(new BarrierStream(package, barrier));
                request.Headers.Add(Constants.NuGetHeaderApiKey, GalleryConfiguration.Instance.Account.ApiKey);
                request.Headers.Add(Constants.NuGetHeaderProtocolVersion, Constants.NuGetProtocolVersion);

                using (var response = await client.SendAsync(request))
                {
                    if (response.StatusCode != HttpStatusCode.Created &&
                        response.StatusCode != HttpStatusCode.Conflict)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        TestOutputHelper.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{content}");
                    }

                    return response.StatusCode;
                }
            }
        }

        private class BarrierStream : Stream
        {
            private readonly Stream _innerStream;
            private readonly Barrier _barrier;

            public BarrierStream(Stream innerStream, Barrier barrier)
            {
                _innerStream = innerStream;
                _barrier = barrier;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => _innerStream.Length;

            public override long Position
            {
                get
                {
                    return _innerStream.Position;
                }
                set
                {
                    _innerStream.Position = value;
                }
            }

            public override void Flush()
            {
                throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _innerStream.Read(buffer, offset, count);
                return GetReadAndWait(read);
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                var read = await _innerStream.ReadAsync(buffer, offset, count);
                return GetReadAndWait(read);
            }

            private int GetReadAndWait(int read)
            {
                // Wait for the event once the entire inner stream has been consumed.
                if (read == 0)
                {
                    _barrier.SignalAndWait();
                }

                return read;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
