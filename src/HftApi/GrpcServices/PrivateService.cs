using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using AutoMapper;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using HftApi.Common.Domain.MyNoSqlEntities;
using HftApi.Extensions;
using JetBrains.Annotations;
using Lykke.HftApi.ApiContract;
using Lykke.HftApi.Domain;
using Lykke.HftApi.Domain.Services;
using Lykke.HftApi.Services;
using Lykke.MatchingEngine.Connector.Abstractions.Services;
using Lykke.MatchingEngine.Connector.Models.Api;
using Lykke.MatchingEngine.Connector.Models.Common;
using Lykke.Service.History.Contracts.Enums;
using Microsoft.AspNetCore.Authorization;
using MyNoSqlServer.Abstractions;
using LimitOrderResponse = Lykke.HftApi.ApiContract.LimitOrderResponse;
using MarketOrderResponse = Lykke.HftApi.ApiContract.MarketOrderResponse;

namespace HftApi.GrpcServices
{
    [Authorize]
    [UsedImplicitly]
    public class PrivateService : Lykke.HftApi.ApiContract.PrivateService.PrivateServiceBase
    {
        private readonly HistoryHttpClient _historyClient;
        private readonly IBalanceService _balanceService;
        private readonly ValidationService _validationService;
        private readonly IMatchingEngineClient _matchingEngineClient;
        private readonly IStreamService<BalanceUpdate> _balanceUpdateService;
        private readonly IStreamService<OrderUpdate> _orderUpdateService;
        private readonly IStreamService<TradeUpdate> _tradeUpdateService;
        private readonly IMyNoSqlServerDataReader<OrderEntity> _ordersReader;
        private readonly IMapper _mapper;

        public PrivateService(
            HistoryHttpClient historyClient,
            IBalanceService balanceService,
            ValidationService validationService,
            IMatchingEngineClient matchingEngineClient,
            IStreamService<BalanceUpdate> balanceUpdateService,
            IStreamService<OrderUpdate> orderUpdateService,
            IStreamService<TradeUpdate> tradeUpdateService,
            IMyNoSqlServerDataReader<OrderEntity> ordersReader,
            IMapper mapper
            )
        {
            _historyClient = historyClient;
            _balanceService = balanceService;
            _validationService = validationService;
            _matchingEngineClient = matchingEngineClient;
            _balanceUpdateService = balanceUpdateService;
            _orderUpdateService = orderUpdateService;
            _tradeUpdateService = tradeUpdateService;
            _ordersReader = ordersReader;
            _mapper = mapper;
        }

        public override async Task<BalancesResponse> GetBalances(Empty request, ServerCallContext context)
        {
            var walletId = context.GetHttpContext().User.GetWalletId();
            var balances = await _balanceService.GetBalancesAsync(walletId);

            var res = new BalancesResponse();
            res.Payload.AddRange(_mapper.Map<List<Balance>>(balances));

            return res;
        }

        public override async Task<LimitOrderResponse> PlaceLimitOrder(LimitOrderRequest request, ServerCallContext context)
        {
            var walletId = context.GetHttpContext().User.GetWalletId();

            var result = await _validationService.ValidateLimitOrderAsync(request.AssetPairId, Convert.ToDecimal(request.Price), Convert.ToDecimal(request.Volume));

            if (result != null)
            {
                return new LimitOrderResponse
                {
                    Error = new Error
                    {
                        Code = (int)result.Code,
                        Message = result.Message
                    }
                };
            }

            var order = new LimitOrderModel
            {
                Id = Guid.NewGuid().ToString(),
                AssetPairId = request.AssetPairId,
                ClientId = walletId,
                Price = Convert.ToDouble(request.Price),
                CancelPreviousOrders = false,
                Volume = Math.Abs(Convert.ToDouble(request.Volume)),
                OrderAction = request.Side == Side.Buy ? OrderAction.Buy : OrderAction.Sell
            };

            MeResponseModel response = await _matchingEngineClient.PlaceLimitOrderAsync(order);

            if (response == null)
            {
                return new LimitOrderResponse
                {
                    Error = new Error
                    {
                        Code = (int)HftApiErrorCode.MeRuntime,
                        Message = "ME not available"
                    }
                };
            }

            (HftApiErrorCode code, string message) = response.Status.ToHftApiError();

            if (code == HftApiErrorCode.Success)
                return new LimitOrderResponse
                {
                    Payload = new LimitOrderResponse.Types.LimitOrderPayload
                    {
                        OrderId = response.TransactionId
                    }
                };

            return new LimitOrderResponse
            {
                Error = new Error
                {
                    Code = (int)code,
                    Message = message
                }
            };
        }

        public override async Task<MarketOrderResponse> PlaceMarketOrder(MarketOrderRequest request, ServerCallContext context)
        {
            var walletId = context.GetHttpContext().User.GetWalletId();

            var result = await _validationService.ValidateMarketOrderAsync(request.AssetPairId, Convert.ToDecimal(request.Volume));

            if (result != null)
            {
                return new MarketOrderResponse
                {
                    Error = new Error
                    {
                        Code = (int)result.Code,
                        Message = result.Message
                    }
                };
            }

            var order = new MarketOrderModel
            {
                Id = Guid.NewGuid().ToString(),
                AssetPairId = request.AssetPairId,
                ClientId = walletId,
                Volume = Math.Abs(Convert.ToDouble(request.Volume)),
                OrderAction = request.Side == Side.Buy ? OrderAction.Buy : OrderAction.Sell,
                Straight = true
            };

            var response = await _matchingEngineClient.HandleMarketOrderAsync(order);

            if (response == null)
            {
                return new MarketOrderResponse
                {
                    Error = new Error
                    {
                        Code = (int)HftApiErrorCode.MeRuntime,
                        Message = "ME not available"
                    }
                };
            }

            (HftApiErrorCode code, string message) = response.Status.ToHftApiError();

            if (code == HftApiErrorCode.Success)
                return new MarketOrderResponse
                {
                    Payload = new MarketOrderResponse.Types.MarketOrderPayload
                    {
                        OrderId = order.Id,
                        Price = response.Price.ToString(CultureInfo.InvariantCulture)
                    }
                };

            return new MarketOrderResponse
            {
                Error = new Error
                {
                    Code = (int)code,
                    Message = message
                }
            };
        }

        public override async Task<OrdersResponse> GetActiveOrders(OrdersRequest request, ServerCallContext context)
        {
            var result = await _validationService.ValidateOrdersRequestAsync(request.AssetPairId, request.Offset, request.Take);

            if (result != null)
            {
                return new OrdersResponse
                {
                    Error = new Error
                    {
                        Code = (int)result.Code,
                        Message = result.Message
                    }
                };
            }

            var statuses = new List<string> {OrderStatus.Placed.ToString(), OrderStatus.PartiallyMatched.ToString()};

            var orders = _ordersReader.Get(context.GetHttpContext().User.GetWalletId(), request.Offset, request.Take,
                x => (string.IsNullOrEmpty(request.AssetPairId) || x.AssetPairId == request.AssetPairId) && statuses.Contains(x.Status));

            var res = new OrdersResponse();
            res.Payload.AddRange(_mapper.Map<List<Order>>(orders));
            return res;
        }

        public override async Task<OrdersResponse> GetClosedOrders(OrdersRequest request, ServerCallContext context)
        {
            var result = await _validationService.ValidateOrdersRequestAsync(request.AssetPairId, request.Offset, request.Take);

            if (result != null)
            {
                return new OrdersResponse
                {
                    Error = new Error
                    {
                        Code = (int)result.Code,
                        Message = result.Message
                    }
                };
            }

            var orders = await _historyClient.GetOrdersByWalletAsync(context.GetHttpContext().User.GetWalletId(), request.AssetPairId,
                new [] { OrderStatus.Matched, OrderStatus.Cancelled }, null, false, request.Offset, request.Take);

            var res = new OrdersResponse();

            res.Payload.AddRange(_mapper.Map<List<Order>>(orders));

            return res;
        }

        public override async Task<CancelOrderResponse> CancelAllOrders(CancelOrdersRequest request, ServerCallContext context)
        {
            var result = await _validationService.ValidateAssetPairAsync(request.AssetPairId);

            if (result != null)
            {
                return new CancelOrderResponse
                {
                    Error = new Error
                    {
                        Code = (int)result.Code,
                        Message = result.Message
                    }
                };
            }

            bool? isBuy;
            switch (request.Side)
            {
                case Side.Buy:
                    isBuy = true;
                    break;
                case Side.Sell:
                    isBuy = false;
                    break;
                default:
                    isBuy = null;
                    break;
            }

            var model = new LimitOrderMassCancelModel
            {
                Id = Guid.NewGuid().ToString(),
                AssetPairId = request.AssetPairId,
                ClientId = context.GetHttpContext().User.GetWalletId(),
                IsBuy = isBuy
            };

            MeResponseModel response = await _matchingEngineClient.MassCancelLimitOrdersAsync(model);

            if (response == null)
            {
                return new CancelOrderResponse
                {
                    Error = new Error
                    {
                        Code = (int)HftApiErrorCode.MeRuntime,
                        Message = "ME not available"
                    }
                };
            }

            (HftApiErrorCode code, string message) = response.Status.ToHftApiError();

            if (code == HftApiErrorCode.Success)
                return new CancelOrderResponse
                {
                    Payload = true
                };

            return new CancelOrderResponse
            {
                Error = new Error
                {
                    Code = (int)code,
                    Message = message
                }
            };
        }

        public override async Task<CancelOrderResponse> CancelOrder(CancelOrderRequest request, ServerCallContext context)
        {
            MeResponseModel response = await _matchingEngineClient.CancelLimitOrderAsync(request.OrderId);

            if (response == null)
            {
                return new CancelOrderResponse
                {
                    Error = new Error
                    {
                        Code = (int)HftApiErrorCode.MeRuntime,
                        Message = "ME not available"
                    }
                };
            }

            (HftApiErrorCode code, string message) = response.Status.ToHftApiError();

            if (code == HftApiErrorCode.Success)
                return new CancelOrderResponse
                {
                    Payload = true
                };

            return new CancelOrderResponse
            {
                Error = new Error
                {
                    Code = (int)code,
                    Message = message
                }
            };
        }

        public override async Task<TradesResponse> GetTrades(TradesRequest request, ServerCallContext context)
        {
            var result = await _validationService.ValidateTradesRequestAsync(request.AssetPairId, request.Offset, request.Take);

            if (result != null)
            {
                return new TradesResponse
                {
                    Error = new Error
                    {
                        Code = (int)result.Code,
                        Message = result.Message
                    }
                };
            }

            DateTime? from = null;
            DateTime? to = null;

            if (DateTime.TryParse(request.From, out var fromDate))
                from = fromDate;

            if (DateTime.TryParse(request.To, out var toDate))
                to = toDate;

            var orderAction = request.OptionalSideCase == TradesRequest.OptionalSideOneofCase.None
                ? (OrderAction?) null
                : request.Side == Side.Buy ? OrderAction.Buy : OrderAction.Sell;
            var trades = await _historyClient.GetTradersAsync(context.GetHttpContext().User.GetWalletId(),
                request.AssetPairId, request.Offset, request.Take, orderAction, from, to);

            var res = new TradesResponse();
            var data = _mapper.Map<List<Trade>>(trades);
            res.Payload.AddRange(data);

            return res;
        }

        public override async Task<TradesResponse> GetOrderTrades(OrderTradesRequest request, ServerCallContext context)
        {
            var trades = await _historyClient.GetOrderTradesAsync(context.GetHttpContext().User.GetWalletId(), request.OrderId);

            var res = new TradesResponse();
            var data = _mapper.Map<List<Trade>>(trades);
            res.Payload.AddRange(data);

            return res;
        }

        public override async Task GetBalanceUpdates(Empty request, IServerStreamWriter<BalanceUpdate> responseStream, ServerCallContext context)
        {
            Console.WriteLine($"New balance stream connect. peer:{context.Peer}");

            string walletId = context.GetHttpContext().User.GetWalletId();

            var balances = await _balanceService.GetBalancesAsync(walletId);

            var streamInfo = new StreamInfo<BalanceUpdate>
            {
                Stream = responseStream,
                CancelationToken = context.CancellationToken,
                Key = context.GetHttpContext().User.GetWalletId(),
                Peer = context.Peer
            };

            var initData = new BalanceUpdate();
            initData.Balances.AddRange(_mapper.Map<List<Balance>>(balances));
            await _balanceUpdateService.RegisterStream(streamInfo, initData);
        }

        public override Task GetOrderUpdates(Empty request, IServerStreamWriter<OrderUpdate> responseStream, ServerCallContext context)
        {
            Console.WriteLine($"New order stream connect. peer:{context.Peer}");

            var streamInfo = new StreamInfo<OrderUpdate>
            {
                Stream = responseStream,
                CancelationToken = context.CancellationToken,
                Key = context.GetHttpContext().User.GetWalletId(),
                Peer = context.Peer
            };

            return _orderUpdateService.RegisterStream(streamInfo);
        }

        public override Task GetTradeUpdates(Empty request, IServerStreamWriter<TradeUpdate> responseStream, ServerCallContext context)
        {
            Console.WriteLine($"New trade stream connect. peer:{context.Peer}");

            var streamInfo = new StreamInfo<TradeUpdate>
            {
                Stream = responseStream,
                CancelationToken = context.CancellationToken,
                Key = context.GetHttpContext().User.GetWalletId(),
                Peer = context.Peer
            };

            return _tradeUpdateService.RegisterStream(streamInfo);
        }
    }
}
