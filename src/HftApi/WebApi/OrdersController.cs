using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoMapper;
using HftApi.Common.Domain.MyNoSqlEntities;
using HftApi.Extensions;
using HftApi.WebApi.Models;
using HftApi.WebApi.Models.Request;
using HftApi.WebApi.Models.Response;
using Lykke.HftApi.Domain;
using Lykke.HftApi.Domain.Exceptions;
using Lykke.HftApi.Services;
using Lykke.MatchingEngine.Connector.Abstractions.Services;
using Lykke.MatchingEngine.Connector.Models.Api;
using Lykke.MatchingEngine.Connector.Models.Common;
using Lykke.Service.History.Contracts.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MyNoSqlServer.Abstractions;
using MarketOrderResponse = HftApi.WebApi.Models.Response.MarketOrderResponse;

namespace HftApi.WebApi
{
    [ApiController]
    [Authorize]
    [Route("api/orders")]
    public class OrdersController : ControllerBase
    {
        private readonly HistoryHttpClient _historyClient;
        private readonly ValidationService _validationService;
        private readonly IMatchingEngineClient _matchingEngineClient;
        private readonly IMyNoSqlServerDataReader<OrderEntity> _ordersReader;
        private readonly IMapper _mapper;

        public OrdersController(
            HistoryHttpClient historyClient,
            ValidationService validationService,
            IMatchingEngineClient matchingEngineClient,
            IMyNoSqlServerDataReader<OrderEntity> ordersReader,
            IMapper mapper
            )
        {
            _historyClient = historyClient;
            _validationService = validationService;
            _matchingEngineClient = matchingEngineClient;
            _ordersReader = ordersReader;
            _mapper = mapper;
        }

        [HttpPost("limit")]
        [ProducesResponseType(typeof(ResponseModel<LimitOrderResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> PlaceLimitOrder(PlaceLimitOrderRequest request)
        {
            var result = await _validationService.ValidateLimitOrderAsync(request.AssetPairId, request.Price, request.Volume);

            if (result != null)
                throw HftApiException.Create(result.Code, result.Message).AddField(result.FieldName);

            var walletId = User.GetWalletId();

            var order = new LimitOrderModel
            {
                Id = Guid.NewGuid().ToString(),
                AssetPairId = request.AssetPairId,
                ClientId = walletId,
                Price = (double)request.Price,
                CancelPreviousOrders = false,
                Volume = (double)Math.Abs(request.Volume),
                OrderAction = request.Side
            };

            MeResponseModel response = await _matchingEngineClient.PlaceLimitOrderAsync(order);

            if (response == null)
                throw HftApiException.Create(HftApiErrorCode.MeRuntime, "ME not available");

            (HftApiErrorCode code, string message) = response.Status.ToHftApiError();

            if (code == HftApiErrorCode.Success)
                return Ok(ResponseModel<LimitOrderResponse>.Ok(new LimitOrderResponse {OrderId = response.TransactionId}));

            throw HftApiException.Create(code, message);
        }

        [HttpPost("market")]
        [ProducesResponseType(typeof(ResponseModel<MarketOrderResponse>), StatusCodes.Status200OK)]
        public async Task<IActionResult> PlaceMarketOrder(PlaceMarketOrderRequest request)
        {
            var result = await _validationService.ValidateMarketOrderAsync(request.AssetPairId, request.Volume);

            if (result != null)
                throw HftApiException.Create(result.Code, result.Message).AddField(result.FieldName);

            var walletId = User.GetWalletId();

            var order = new MarketOrderModel
            {
                Id = Guid.NewGuid().ToString(),
                AssetPairId = request.AssetPairId,
                ClientId = walletId,
                Volume = (double)request.Volume,
                OrderAction = request.Side,
                Straight = true
            };

            var response = await _matchingEngineClient.HandleMarketOrderAsync(order);

            if (response == null)
                throw HftApiException.Create(HftApiErrorCode.MeRuntime, "ME not available");

            (HftApiErrorCode code, string message) = response.Status.ToHftApiError();

            if (code == HftApiErrorCode.Success)
            {
                return Ok(ResponseModel<MarketOrderResponse>.Ok(new MarketOrderResponse {OrderId = order.Id, Price = (decimal)response.Price}));
            }

            throw HftApiException.Create(code, message);
        }

        [HttpGet("active")]
        [ProducesResponseType(typeof(ResponseModel<IReadOnlyCollection<OrderModel>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetActiveOrders(
            [FromQuery]string assetPairId = null,
            [FromQuery]int? offset = 0,
            [FromQuery]int? take = 100
            )
        {
            var result = await _validationService.ValidateOrdersRequestAsync(assetPairId, offset, take);

            if (result != null)
                throw HftApiException.Create(result.Code, result.Message).AddField(result.FieldName);

            var statuses = new List<string> {OrderStatus.Placed.ToString(), OrderStatus.PartiallyMatched.ToString()};

            var orders = _ordersReader.Get(User.GetWalletId(), offset ?? 0, take ?? 100,
                x => (string.IsNullOrEmpty(assetPairId) || x.AssetPairId == assetPairId) && statuses.Contains(x.Status));

            var ordersModel = _mapper.Map<IReadOnlyCollection<OrderModel>>(orders);

            return Ok(ResponseModel<IReadOnlyCollection<OrderModel>>.Ok(ordersModel));
        }

        [HttpGet("closed")]
        [ProducesResponseType(typeof(ResponseModel<IReadOnlyCollection<OrderModel>>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetCloasedOrders(
            [FromQuery]string assetPairId = null,
            [FromQuery]int? offset = 0,
            [FromQuery]int? take = 100
            )
        {
            var result = await _validationService.ValidateOrdersRequestAsync(assetPairId, offset, take);

            if (result != null)
                throw HftApiException.Create(result.Code, result.Message).AddField(result.FieldName);

            var orders = await _historyClient.GetOrdersByWalletAsync(User.GetWalletId(), assetPairId,
                new [] { OrderStatus.Matched, OrderStatus.Cancelled }, null, false, offset, take );

            var ordersModel = _mapper.Map<IReadOnlyCollection<OrderModel>>(orders);

            return Ok(ResponseModel<IReadOnlyCollection<OrderModel>>.Ok(_mapper.Map<IReadOnlyCollection<OrderModel>>(ordersModel)));
        }

        [HttpDelete]
        [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
        public async Task<IActionResult> CancelAllOrders([FromQuery]string assetPairId = null, [FromQuery]OrderAction? side = null)
        {
            var assetPairResult = await _validationService.ValidateAssetPairAsync(assetPairId);

            if (assetPairResult != null)
                throw HftApiException.Create(assetPairResult.Code, assetPairResult.Message).AddField(assetPairResult.FieldName);

            bool? isBuy;
            switch (side)
            {
                case OrderAction.Buy:
                    isBuy = true;
                    break;
                case OrderAction.Sell:
                    isBuy = false;
                    break;
                default:
                    isBuy = null;
                    break;
            }

            var model = new LimitOrderMassCancelModel
            {
                Id = new Guid().ToString(),
                AssetPairId = assetPairId,
                ClientId = User.GetWalletId(),
                IsBuy = isBuy
            };

            MeResponseModel response = await _matchingEngineClient.MassCancelLimitOrdersAsync(model);

            if (response == null)
                throw HftApiException.Create(HftApiErrorCode.MeRuntime, "ME not available");

            (HftApiErrorCode code, string message) = response.Status.ToHftApiError();

            if (code == HftApiErrorCode.Success)
                return Ok();

            throw HftApiException.Create(code, message);
        }

        [HttpDelete("{orderId}")]
        [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
        public async Task<IActionResult> CancelOrder(string orderId)
        {
            MeResponseModel response = await _matchingEngineClient.CancelLimitOrderAsync(orderId);

            if (response == null)
                throw HftApiException.Create(HftApiErrorCode.MeRuntime, "ME not available");

            (HftApiErrorCode code, string message) = response.Status.ToHftApiError();

            if (code == HftApiErrorCode.Success)
                return Ok();

            throw HftApiException.Create(code, message);
        }
    }
}
