using System;
using System.Collections.Generic;
using AutoMapper;
using Lykke.HftApi.ApiContract;
using Lykke.HftApi.Domain.Entities;
using Lykke.HftApi.Domain.Services;
using MyNoSqlServer.Abstractions;
using MyNoSqlServer.DataReader;
using Orderbook = Lykke.HftApi.ApiContract.Orderbook;

namespace HftApi
{
    public class ApplicationManager
    {
        private readonly MyNoSqlTcpClient _noSqlTcpClient;
        private readonly IMyNoSqlServerDataReader<PriceEntity> _pricesReader;
        private readonly IMyNoSqlServerDataReader<TickerEntity> _tickerReader;
        private readonly IMyNoSqlServerDataReader<OrderbookEntity> _orderbookReader;
        private readonly IStreamService<PriceUpdate> _priceStraem;
        private readonly IStreamService<TickerUpdate> _tickerStream;
        private readonly IStreamService<Orderbook> _orderbookStream;
        private readonly IMapper _mapper;

        public ApplicationManager(
            MyNoSqlTcpClient noSqlTcpClient,
            IMyNoSqlServerDataReader<PriceEntity> pricesReader,
            IMyNoSqlServerDataReader<TickerEntity> tickerReader,
            IMyNoSqlServerDataReader<OrderbookEntity> orderbookReader,
            IStreamService<PriceUpdate> priceStraem,
            IStreamService<TickerUpdate> tickerStream,
            IStreamService<Orderbook>  orderbookStream,
            IMapper mapper
            )
        {
            _noSqlTcpClient = noSqlTcpClient;
            _pricesReader = pricesReader;
            _tickerReader = tickerReader;
            _orderbookReader = orderbookReader;
            _priceStraem = priceStraem;
            _tickerStream = tickerStream;
            _orderbookStream = orderbookStream;
            _mapper = mapper;
        }

        public void Start()
        {
            _pricesReader.SubscribeToChanges(prices =>
            {
                foreach (var price in prices)
                {
                    _priceStraem.WriteToStream(_mapper.Map<PriceUpdate>(price));
                }
            });

            _tickerReader.SubscribeToChanges(tickers =>
            {
                foreach (var ticker in tickers)
                {
                    _tickerStream.WriteToStream(_mapper.Map<TickerUpdate>(ticker));
                }
            });

            _orderbookReader.SubscribeToChanges(orderbooks =>
            {
                foreach (var orderbook in orderbooks)
                {
                    var item = _mapper.Map<Orderbook>(orderbook);
                    item.Asks.AddRange(_mapper.Map<List<Orderbook.Types.PriceVolume>>(orderbook.Asks));
                    item.Bids.AddRange(_mapper.Map<List<Orderbook.Types.PriceVolume>>(orderbook.Bids));
                    _orderbookStream.WriteToStream(item, orderbook.AssetPairId);
                }
            });

            Console.WriteLine("Stream services started.");
        }

        public void Stop()
        {
            _priceStraem.Stop();
            _tickerStream.Stop();
            _noSqlTcpClient.Stop();
            Console.WriteLine("Stream services stopped.");
        }
    }
}