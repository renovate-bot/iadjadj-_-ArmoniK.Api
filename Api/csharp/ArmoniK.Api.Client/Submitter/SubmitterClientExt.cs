// This file is part of the ArmoniK project
// 
// Copyright (C) ANEO, 2021-2022. All rights reserved.
//   W. Kirschenmann   <wkirschenmann@aneo.fr>
//   J. Gurhem         <jgurhem@aneo.fr>
//   D. Dubuc          <ddubuc@aneo.fr>
//   L. Ziane Khodja   <lzianekhodja@aneo.fr>
//   F. Lemaitre       <flemaitre@aneo.fr>
//   S. Djebbar        <sdjebbar@aneo.fr>
//   J. Fonseca        <jfonseca@aneo.fr>
// 
// Licensed under the Apache License, Version 2.0 (the "License")
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//         http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using ArmoniK.Api.gRPC.V1;
using ArmoniK.Api.gRPC.V1.Submitter;

using Google.Protobuf;

using Grpc.Core;

using JetBrains.Annotations;

namespace ArmoniK.Api.Client.Submitter
{

  [PublicAPI]
  public static class SubmitterClientExt
  {
    public static async Task<CreateTaskReply> CreateTasksAsync(this gRPC.V1.Submitter.Submitter.SubmitterClient client,
                                                               string                                           sessionId,
                                                               TaskOptions?                                     taskOptions,
                                                               IEnumerable<TaskRequest>                         taskRequests,
                                                               CancellationToken                                cancellationToken = default)
    {
      var serviceConfiguration = await client.GetServiceConfigurationAsync(new Empty(),
                                                                           cancellationToken: cancellationToken);

      using var stream = client.CreateLargeTasks(cancellationToken: cancellationToken);

      foreach (var createLargeTaskRequest in taskRequests.ToRequestStream(sessionId,
                                                                          taskOptions,
                                                                          serviceConfiguration.DataChunkMaxSize))
      {
        await stream.RequestStream.WriteAsync(createLargeTaskRequest)
                    .ConfigureAwait(false);
      }

      await stream.RequestStream.CompleteAsync()
                  .ConfigureAwait(false);

      return await stream.ResponseAsync.ConfigureAwait(false);
    }


    private static IEnumerable<CreateLargeTaskRequest> ToRequestStream(this IEnumerable<TaskRequest> taskRequests,
                                                                       string                        sessionId,
                                                                       TaskOptions?                  taskOptions,
                                                                       int                           chunkMaxSize)
    {
      yield return new CreateLargeTaskRequest
                   {
                     InitRequest = new CreateLargeTaskRequest.Types.InitRequest
                                   {
                                     SessionId   = sessionId,
                                     TaskOptions = taskOptions,
                                   },
                   };

      using var taskRequestEnumerator = taskRequests.GetEnumerator();

      if (!taskRequestEnumerator.MoveNext())
      {
        yield break;
      }

      var currentRequest = taskRequestEnumerator.Current;

      while (taskRequestEnumerator.MoveNext())
      {
        foreach (var createLargeTaskRequest in currentRequest.ToRequestStream(false,
                                                                              chunkMaxSize))
        {
          yield return createLargeTaskRequest;
        }


        currentRequest = taskRequestEnumerator.Current;
      }

      foreach (var createLargeTaskRequest in currentRequest.ToRequestStream(true,
                                                                            chunkMaxSize))
      {
        yield return createLargeTaskRequest;
      }
    }

    private static IEnumerable<CreateLargeTaskRequest> ToRequestStream(this TaskRequest taskRequest,
                                                                       bool             isLast,
                                                                       int              chunkMaxSize)
    {
      yield return new CreateLargeTaskRequest
                   {
                     InitTask = new InitTaskRequest
                                {
                                  Header = new TaskRequestHeader
                                           {
                                             DataDependencies =
                                             {
                                               taskRequest.DataDependencies,
                                             },
                                             ExpectedOutputKeys =
                                             {
                                               taskRequest.ExpectedOutputKeys,
                                             },
                                             Id = taskRequest.Id,
                                           },
                                },
                   };

      var start = 0;

      if (taskRequest.Payload.Length == 0)
      {
        yield return new CreateLargeTaskRequest
                     {
                       TaskPayload = new DataChunk
                                     {
                                       Data = ByteString.Empty,
                                     },
                     };
      }

      while (start < taskRequest.Payload.Length)
      {
        var chunkSize = Math.Min(chunkMaxSize,
                                 taskRequest.Payload.Length - start);

        yield return new CreateLargeTaskRequest
                     {
                       TaskPayload = new DataChunk
                                     {
                                       Data = ByteString.CopyFrom(taskRequest.Payload.Span.Slice(start,
                                                                                                 chunkSize)),
                                     },
                     };

        start += chunkSize;
      }

      yield return new CreateLargeTaskRequest
                   {
                     TaskPayload = new DataChunk
                                   {
                                     DataComplete = true,
                                   },
                   };

      if (isLast)
      {
        yield return new CreateLargeTaskRequest
                     {
                       InitTask = new InitTaskRequest
                                  {
                                    LastTask = true,
                                  },
                     };
      }
    }

    public static async Task<byte[]> GetResultAsync(this gRPC.V1.Submitter.Submitter.SubmitterClient client,
                                                    ResultRequest                                    resultRequest,
                                                    CancellationToken                                cancellationToken = default)
    {
      var streamingCall = client.TryGetResultStream(resultRequest,
                                                    cancellationToken: cancellationToken);

      var result = new List<byte>();

      await foreach (var reply in streamingCall.ResponseStream.ReadAllAsync(cancellationToken)
                                               .ConfigureAwait(false))
      {
        switch (reply.TypeCase)
        {
          case ResultReply.TypeOneofCase.Result:
            if (!reply.Result.DataComplete)
            {
              result.AddRange(reply.Result.Data.ToByteArray());
            }

            break;
          case ResultReply.TypeOneofCase.None:
            throw new Exception("Issue with Server !");
          case ResultReply.TypeOneofCase.Error:
            throw new Exception($"Error in task {reply.Error.TaskId}");
          case ResultReply.TypeOneofCase.NotCompletedTask:
            throw new Exception($"Task {reply.NotCompletedTask} not completed");
          default:
            throw new ArgumentOutOfRangeException();
        }
      }

      return result.ToArray();
    }
  }

}
