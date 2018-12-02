using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Foundatio.Caching;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netsphere.Common;
using Netsphere.Common.Configuration;
using Netsphere.Database;
using Netsphere.Database.Auth;
using Netsphere.Database.Game;
using Netsphere.Network;
using Netsphere.Network.Message.Game;
using Netsphere.Server.Game.Rules;
using Netsphere.Server.Game.Services;
using ProudNet;

namespace Netsphere.Server.Game.Handlers
{
    internal class AuthenticationHandler
        : IHandle<CLoginReqMessage>,
          IHandle<CCheckNickReqMessage>,
          IHandle<CCreateNickReqMessage>
    {
        private readonly ILogger _logger;
        private readonly NetworkOptions _networkOptions;
        private readonly AppOptions _appOptions;
        private readonly GameOptions _gameOptions;
        private readonly ICacheClient _cacheClient;
        private readonly ISessionManager _sessionManager;
        private readonly IDatabaseProvider _databaseProvider;
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerManager _playerManager;
        private readonly GameDataService _gameDataService;

        public AuthenticationHandler(ILogger<AuthenticationHandler> logger,
            IOptions<NetworkOptions> networkOptions, IOptions<AppOptions> appOptions, IOptions<GameOptions> gameOptions,
            ICacheClient cacheClient, ISessionManager sessionManager, IDatabaseProvider databaseProvider,
            IServiceProvider serviceProvider, PlayerManager playerManager, GameDataService gameDataService)
        {
            _logger = logger;
            _networkOptions = networkOptions.Value;
            _appOptions = appOptions.Value;
            _gameOptions = gameOptions.Value;
            _cacheClient = cacheClient;
            _sessionManager = sessionManager;
            _databaseProvider = databaseProvider;
            _serviceProvider = serviceProvider;
            _playerManager = playerManager;
            _gameDataService = gameDataService;
        }

        [Firewall(typeof(MustBeLoggedIn), Invert = true)]
        public async Task<bool> OnHandle(MessageContext context, CLoginReqMessage message)
        {
            var session = context.GetSession<Session>();

            using (_logger.BeginScope(
                "RemoteEndPoint={RemoteEndPoint} Message={Message}",
                session.RemoteEndPoint.ToString(), message.ToJson()))
            {
                _logger.LogDebug("Login from {RemoteEndPoint}");

                if (_appOptions.ClientVersions.All(x => message.Version != x))
                {
                    _logger.LogInformation("Invalid client version={Version} supported versions are {SupportedVersions}",
                        message.Version.ToString(), string.Join(",", _appOptions.ClientVersions.Select(x => x.ToString())));
                    await session.SendAsync(new SLoginAckMessage(GameLoginResult.WrongVersion));
                    await session.CloseAsync();
                    return true;
                }

                if (_sessionManager.Sessions.Count >= _networkOptions.MaxSessions)
                {
                    await session.SendAsync(new SLoginAckMessage(GameLoginResult.ServerFull));
                    return true;
                }

                AccountEntity accountEntity;
                using (var db = _databaseProvider.Open<AuthContext>())
                {
                    var accountId = (int)message.AccountId;
                    accountEntity = await db.Accounts
                        .LoadWith(x => x.Bans)
                        .FirstOrDefaultAsync(x => x.Id == accountId);
                }

                if (accountEntity == null)
                {
                    _logger.LogInformation("Wrong login");
                    await session.SendAsync(new SLoginAckMessage(GameLoginResult.SessionTimeout));
                    return true;
                }

                // Validate session
                var sessionId = await _cacheClient.GetAsync<string>(Constants.Cache.SessionKey(accountEntity.Id));
                if (!sessionId.HasValue || !sessionId.Value.Equals(message.SessionId))
                {
                    _logger.LogInformation("Invalid session id");
                    await session.SendAsync(new SLoginAckMessage(GameLoginResult.SessionTimeout));
                    return true;
                }

                // Check ban status
                var now = DateTimeOffset.Now.ToUnixTimeSeconds();
                var ban = accountEntity.Bans.FirstOrDefault(x => x.Duration == null || x.Date + x.Duration > now);
                if (ban != null)
                {
                    var unbanDate = DateTimeOffset.MinValue;
                    if (ban.Duration != null)
                        unbanDate = DateTimeOffset.FromUnixTimeSeconds(ban.Date + (ban.Duration ?? 0));

                    _logger.LogInformation("Account is banned until {UnbanDate}", unbanDate);
                    await session.SendAsync(new SLoginAckMessage(GameLoginResult.SessionTimeout));
                    return true;
                }

                var account = new Account((ulong)accountEntity.Id, accountEntity.Username, accountEntity.Nickname,
                    (SecurityLevel)accountEntity.SecurityLevel);

                if (message.KickConnection)
                {
                    var oldPlr = _playerManager[account.Id];
                    if (oldPlr != null)
                    {
                        _logger.LogInformation("Kicking old connection hostId={HostId}", oldPlr.Session.HostId);
                        await oldPlr.DisconnectAsync();
                    }
                }

                if (_playerManager.Contains(account.Id))
                {
                    // TODO Check if logged in on another server

                    _logger.LogInformation("Account is already logged in");
                    await session.SendAsync(new SLoginAckMessage(GameLoginResult.TerminateOtherConnection));
                    return true;
                }

                using (var db = _databaseProvider.Open<GameContext>())
                {
                    var plr = await db.Players
                        .LoadWith(x => x.Characters)
                        .LoadWith(x => x.Items)
                        .LoadWith(x => x.Licenses)
                        .FirstOrDefaultAsync(x => x.Id == accountEntity.Id);

                    if (plr == null)
                    {
                        var levelInfo = _gameDataService.Levels.GetValueOrDefault(_gameOptions.StartLevel);
                        if (levelInfo == null)
                            _logger.LogWarning("Invalid StartLevel={StartLevel} in config", _gameOptions.StartLevel);

                        plr = new PlayerEntity
                        {
                            Id = (int)account.Id,
                            AP = _gameOptions.StartAP,
                            PEN = _gameOptions.StartPEN,
                            Coins1 = _gameOptions.StartCoins1,
                            Coins2 = _gameOptions.StartCoins2,
                            TotalExperience = (int)(levelInfo?.TotalExperience ?? 0)
                        };

                        await db.InsertAsync(plr);
                    }

                    session.Player = _serviceProvider.GetRequiredService<Player>();
                    session.Player.Initialize(session, account, plr);
                    session.SessionId = message.SessionId;
                }

                _playerManager.Add(session.Player);
                _logger.LogInformation("Login success");

                var result = string.IsNullOrWhiteSpace(account.Nickname)
                    ? GameLoginResult.ChooseNickname
                    : GameLoginResult.OK;
                await session.SendAsync(new SLoginAckMessage(result, account.Id));

                if (!string.IsNullOrWhiteSpace(account.Nickname))
                    await session.Player.SendAccountInformation();
            }

            return true;
        }

        [Firewall(typeof(MustNotHaveANickname))]
        public async Task<bool> OnHandle(MessageContext context, CCheckNickReqMessage message)
        {
            var session = context.Session;

            using (_logger.BeginScope("RemoteEndPoint={RemoteEndPoint} Nickname={Nickname}",
                session.RemoteEndPoint.ToString(), message.Nickname))
            {
                var available = await IsNickAvailableAsync(message.Nickname);
                if (!available)
                    _logger.LogDebug("Nickname not available");

                await session.SendAsync(new SCheckNickAckMessage(!available));
            }

            return true;
        }

        [Firewall(typeof(MustNotHaveANickname))]
        public async Task<bool> OnHandle(MessageContext context, CCreateNickReqMessage message)
        {
            var session = (Session)context.Session;

            using (_logger.BeginScope("RemoteEndPoint={RemoteEndPoint} Nickname={Nickname}",
                session.RemoteEndPoint.ToString(), message.Nickname))
            {
                var available = await IsNickAvailableAsync(message.Nickname);
                if (!available)
                {
                    _logger.LogDebug("Nickname not available");
                    await session.SendAsync(new SCheckNickAckMessage(true));
                }
            }

            session.Player.Account.Nickname = message.Nickname;
            using (var db = _databaseProvider.Open<AuthContext>())
            {
                var accountId = (int)session.Player.Account.Id;
                await db.Accounts
                    .Where(x => x.Id == accountId)
                    .Set(x => x.Nickname, message.Nickname)
                    .UpdateAsync();
            }

            await session.SendAsync(new SServerResultInfoAckMessage(ServerResult.CreateNicknameSuccess));
            await session.Player.SendAccountInformation();
            return true;
        }

        private async Task<bool> IsNickAvailableAsync(string nickname)
        {
            var minLength = _gameOptions.NickRestrictions.MinLength;
            var maxLength = _gameOptions.NickRestrictions.MaxLength;
            var whitespace = _gameOptions.NickRestrictions.WhitespaceAllowed;
            var ascii = _gameOptions.NickRestrictions.AsciiOnly;

            if (string.IsNullOrWhiteSpace(nickname) ||
                !whitespace && nickname.Contains(" ") ||
                nickname.Length < minLength ||
                nickname.Length > maxLength ||
                ascii && Encoding.UTF8.GetByteCount(nickname) != nickname.Length)
            {
                return false;
            }

            // check for repeating chars example: (AAAHello, HeLLLLo)
            var maxRepeat = _gameOptions.NickRestrictions.MaxRepeat;
            if (maxRepeat > 0)
            {
                var counter = 1;
                var current = nickname[0];
                for (var i = 1; i < nickname.Length; i++)
                {
                    if (current != nickname[i])
                    {
                        if (counter > maxRepeat)
                            return false;

                        counter = 0;
                        current = nickname[i];
                    }

                    ++counter;
                }

                if (counter > maxRepeat)
                    return false;
            }

            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            using (var db = _databaseProvider.Open<AuthContext>())
            {
                var nickExists = await db.Accounts.AnyAsync(x => x.Nickname == nickname);
                var nickReserved = await db.Nicknames.AnyAsync(x =>
                    x.Nickname == nickname && (x.ExpireDate == -1 || x.ExpireDate > now));
                return !nickExists && !nickReserved;
            }
        }
    }
}
