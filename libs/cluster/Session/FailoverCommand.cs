﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Text;
using System.Threading;
using Garnet.common;
using Garnet.server;

namespace Garnet.cluster
{
    internal sealed unsafe partial class ClusterSession : IClusterSession
    {
        private bool TryFAILOVER(int count, byte* ptr)
        {
            var args = count - 1;
            var replicaAddress = string.Empty;
            var replicaPort = 0;
            var timeout = -1;
            var abort = false;
            var force = false;

            while (args > 0)
            {
                if (!RespReadUtils.ReadStringWithLengthHeader(out var option, ref ptr, recvBufferPtr + bytesRead))
                    return false;

                FailoverOption failoverOption;
                try
                {
                    failoverOption = (FailoverOption)Enum.Parse(typeof(FailoverOption), option);
                }
                catch
                {
                    failoverOption = FailoverOption.INVALID;
                }

                args--;
                if (failoverOption == FailoverOption.INVALID)
                    continue;

                switch (failoverOption)
                {
                    case FailoverOption.TO:
                        // 1. Address
                        if (!RespReadUtils.ReadStringWithLengthHeader(out replicaAddress, ref ptr, recvBufferPtr + bytesRead))
                            return false;

                        // 2. Port
                        if (!RespReadUtils.ReadIntWithLengthHeader(out replicaPort, ref ptr, recvBufferPtr + bytesRead))
                            return false;

                        args -= 2;
                        break;
                    case FailoverOption.TIMEOUT:
                        if (!RespReadUtils.ReadIntWithLengthHeader(out timeout, ref ptr, recvBufferPtr + bytesRead))
                            return false;
                        break;
                    case FailoverOption.ABORT:
                        abort = true;
                        break;
                    case FailoverOption.FORCE:
                        force = true;
                        break;
                    default:
                        throw new Exception($"Failover option {failoverOption} not supported");
                }
            }
            readHead = (int)(ptr - recvBufferPtr);

            if (clusterProvider.clusterManager.CurrentConfig.GetLocalNodeRole() != NodeRole.PRIMARY)
            {
                var resp = CmdStrings.RESP_CANNOT_FAILOVER_FROM_NON_MASTER;
                while (!RespWriteUtils.WriteDirect(resp, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            // Validate failing over node config
            if (replicaPort != -1 && replicaAddress != string.Empty)
            {
                var replicaNodeId = clusterProvider.clusterManager.CurrentConfig.GetWorkerNodeIdFromAddress(replicaAddress, replicaPort);
                if (replicaNodeId == null)
                {
                    var resp = CmdStrings.RESP_UNKNOWN_ENDPOINT_ERROR;
                    while (!RespWriteUtils.WriteDirect(resp, ref dcurr, dend))
                        SendAndReset();
                    return true;
                }

                var worker = clusterProvider.clusterManager.CurrentConfig.GetWorkerFromNodeId(replicaNodeId);
                if (worker.role != NodeRole.REPLICA)
                {
                    var resp = Encoding.ASCII.GetBytes($"-ERR Node @{replicaAddress}:{replicaPort} is not a replica.\r\n");
                    while (!RespWriteUtils.WriteDirect(resp, ref dcurr, dend))
                        SendAndReset();
                    return true;
                }

                if (worker.replicaOfNodeId != clusterProvider.clusterManager.CurrentConfig.GetLocalNodeId())
                {
                    var resp = Encoding.ASCII.GetBytes($"-ERR Node @{replicaAddress}:{replicaPort} is not my replica.\r\n");
                    while (!RespWriteUtils.WriteDirect(resp, ref dcurr, dend))
                        SendAndReset();
                    return true;
                }
            }

            // Try abort ongoing failover
            if (abort)
            {
                clusterProvider.clusterManager.TrySetLocalNodeRole(NodeRole.PRIMARY);
                clusterProvider.failoverManager.TryAbortReplicaFailover();
            }
            else
            {
                var timeoutTimeSpan = timeout <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(timeout);
                _ = clusterProvider.failoverManager.TryStartPrimaryFailover(replicaAddress, replicaPort, force ? FailoverOption.FORCE : FailoverOption.DEFAULT, timeoutTimeSpan);
            }

            while (!RespWriteUtils.WriteDirect(CmdStrings.RESP_OK, ref dcurr, dend))
                SendAndReset();
            return true;
        }
    }
}