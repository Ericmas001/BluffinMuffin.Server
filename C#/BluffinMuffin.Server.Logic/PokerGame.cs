﻿using System;
using System.Linq;
using System.Threading;
using BluffinMuffin.Protocol.DataTypes.Options;
using BluffinMuffin.Server.DataTypes.Enums;
using BluffinMuffin.Server.DataTypes;
using BluffinMuffin.Server.DataTypes.EventHandling;
using BluffinMuffin.Protocol.DataTypes;
using BluffinMuffin.Protocol.DataTypes.Enums;
using BluffinMuffin.Server.Logic.GameModules;
using Com.Ericmas001.Util;

namespace BluffinMuffin.Server.Logic
{
    /// <summary>
    /// This represent only one "Game" of Poker. This means from Putting Blinds to preflop, flop, turn, river and then declaring the winner.
    /// Tipically you would have many "Game" while sitting at a table, but players can sit-in and sit-out, so it's like a different game every "Game".
    /// </summary>
    public class PokerGame : IPokerGame
    {
        #region Fields

        // STATES
        private IGameModule m_CurrentModule;
        private GameStateEnum m_State; // L'etat global de la game
        private RoundStateEnum m_RoundState; // L'etat de la game pour chaque round
        #endregion Fields

        #region Properties
        public PokerGameObserver Observer { get; private set; }

        /// <summary>
        /// The Table Entity
        /// </summary>
        public TableInfo Table { get; private set; }
        /// <summary>
        /// The PokerTable Entity
        /// </summary>
        public PokerTable GameTable { get { return (PokerTable)Table; } }

        /// <summary>
        /// The Rules Entity
        /// </summary>
        public TableParams Params { get; private set; }

        /// <summary>
        /// Current Round of the Playing State
        /// </summary>
        public RoundTypeEnum Round
        {
            get { return Table.Round; }
        }

        /// <summary>
        /// Is the Game currently Running ? (Not Ended)
        /// </summary>
        public bool IsRunning
        {
            get { return m_State != GameStateEnum.End; }
        }

        /// <summary>
        /// Is the Game currently Running ? (Not Ended)
        /// </summary>
        public bool IsPlaying
        {
            get { return IsRunning && m_State >= GameStateEnum.WaitForBlinds; }
        }

        /// <summary>
        /// Current State of the Game
        /// </summary>
        public GameStateEnum State
        {
            get { return m_State; }
        }

        #endregion

        #region Ctors & Init

        public PokerGame(PokerTable table)
            : this(new TexasHoldemDealer(), table)
        {
        }

        private PokerGame(AbstractDealer dealer, PokerTable table)
        {
            Observer = new PokerGameObserver(this);
            table.Dealer = dealer;
            Table = table;
            Params = table.Params;
            m_State = GameStateEnum.Init;
            m_RoundState = RoundStateEnum.Cards;
        }
        #endregion Ctors & Init

        #region Public Methods
        /// <summary>
        /// Starts the Game. Only works in Init State
        /// </summary>
        public void Start()
        {
            if (IsInitializing)
                SetModule(new InitGameModule(Observer, GameTable));
        }

        /// <summary>
        /// Add a player to the table
        /// </summary>
        public bool JoinGame(PlayerInfo p)
        {
            if (m_State == GameStateEnum.Init || m_State == GameStateEnum.End)
            {
                LogManager.Log(LogLevel.Error, "PokerGame.JoinGame", "Can't join, bad timing: {0}", m_State);
                return false;
            }

            Observer.RaisePlayerJoined(p);
            return GameTable.JoinTable(p);
        }

        public int AfterPlayerSat(PlayerInfo p, int noSeat = -1, int moneyAmount = 1500)
        {
            var seat = p.NoSeat == -1 ? null : Table.Seats[p.NoSeat];
            if (seat != null && !seat.IsEmpty)
            {
                Observer.RaiseSeatUpdated(seat.Clone());

                if (m_CurrentModule != null)
                    m_CurrentModule.OnSitIn();
                if (m_State > GameStateEnum.WaitForPlayers)
                    GameTable.NewArrivals.Add(p);
                return p.NoSeat;
            }
            return -1;
        }

        private bool IsWaitingForPlayers
        {
            get { return m_State == GameStateEnum.WaitForPlayers; }
        }

        private bool IsInitializing
        {
            get { return m_State == GameStateEnum.Init; }
        }

        public bool SitOut(PlayerInfo p)
        {
            var oldSeat = p.NoSeat;
            if (oldSeat == -1)
                return true;

            var blindNeeded = GameTable.GetBlindNeeded(p);

            p.State = PlayerStateEnum.Zombie;
            if (State == GameStateEnum.Playing && Table.CurrentPlayer == p)
                PlayMoney(p, -1);
            else if (blindNeeded > 0)
                PlayMoney(p, blindNeeded);

            if (Table.SeatsContainsPlayer(p) && Table.SitOut(p))
            {
                var seat = new SeatInfo()
                {
                    Player = null,
                    NoSeat = oldSeat,
                };
                Observer.RaiseSeatUpdated(seat);
                if (m_CurrentModule != null)
                    m_CurrentModule.OnSitOut();
                return true;
            }
            return false;
        }

        /// <summary>
        /// The player is leaving the game
        /// </summary>
        public void LeaveGame(PlayerInfo p)
        {
            var sitOutOk = SitOut(p);

            if (sitOutOk && Table.LeaveTable(p))
            {
                if (Table.Players.Count == 0)
                    m_State = GameStateEnum.End;
            }
        }

        /// <summary>
        /// The player is putting money in the game
        /// </summary>
        public bool PlayMoney(PlayerInfo p, int amount)
        {
            lock(Table)
            {
                var amnt = Math.Min(amount, p.MoneySafeAmnt);
                LogManager.Log(LogLevel.MessageLow, "PokerGame.PlayMoney", "{0} is playing {1} money on state: {2}", p.Name, amnt, m_State);

                if (m_CurrentModule != null)
                    return m_CurrentModule.OnMoneyPlayed(p, amount);
                if (m_State == GameStateEnum.Playing && m_RoundState == RoundStateEnum.Betting)
                    return BetMoney(p, amnt);

                LogManager.Log(LogLevel.Warning, "PokerGame.PlayMoney", "{0} played money but the game is not in the right state", p.Name);

                return false;
            }
        }
        #endregion

        #region Private Methods

        private void SetModule(IGameModule module)
        {
            m_CurrentModule = module;
            m_CurrentModule.ModuleCompleted += delegate(object sender, SuccessEventArg arg)
            {
                if (arg.Success)
                {
                    AdvanceToNextGameState();
                }
                else
                {
                    m_State = GameStateEnum.End;
                    Observer.RaiseEverythingEnded();
                }
            };
            m_CurrentModule.InitModule();
        }
        
        private void AdvanceToNextGameState()
        {
            if (m_State == GameStateEnum.End)
                return;

            m_State = (GameStateEnum)(((int)m_State) + 1);
            m_CurrentModule = null;

            switch (m_State)
            {
                case GameStateEnum.WaitForPlayers:
                    SetModule(new WaitForPlayerModule(Observer, GameTable));
                    break;
                case GameStateEnum.WaitForBlinds:
                    SetModule(new WaitForBlindsModule(Observer, GameTable));
                    break;
                case GameStateEnum.Playing:
                    Table.Round = RoundTypeEnum.Preflop;
                    m_RoundState = RoundStateEnum.Cards;
                    StartRound();
                    break;
                case GameStateEnum.Showdown:
                    ShowAllCards();
                    break;
                case GameStateEnum.DecideWinners:
                    DecideWinners();
                    break;
                case GameStateEnum.DistributeMoney:
                    DistributeMoney();
                    StartANewGame();
                    break;
                case GameStateEnum.End:
                    Observer.RaiseEverythingEnded();
                    break;
            }
        }
        private void StartANewGame()
        {
            Observer.RaiseGameEnded();
            m_State = GameStateEnum.Init;
            AdvanceToNextGameState();
        }
        private void AdvanceToNextRound()
        {
            if (m_State != GameStateEnum.Playing)
                return;

            if (Table.Round == RoundTypeEnum.River)
                AdvanceToNextGameState(); //Advancing to Showdown State
            else
            {
                m_RoundState = RoundStateEnum.Cards;
                Table.ChangeCurrentPlayerTo(Table.DealerSeat);
                Table.Round = (RoundTypeEnum)(((int)Table.Round) + 1);
                StartRound();
            }
        }
        private void AdvanceToNextRoundState()
        {
            if (m_State != GameStateEnum.Playing)
                return;

            if (m_RoundState == RoundStateEnum.Cumul)
                return;

            m_RoundState = (RoundStateEnum)(((int)m_RoundState) + 1);
            StartRound();
        }
        private bool BetMoney(PlayerInfo p, int amnt)
        {
            LogManager.Log(LogLevel.MessageVeryLow, "PokerGame.PlayMoney", "Currently, we need {0} minimum money from this player", Table.CallAmnt(p));

            //Validation: Is it the player's turn to play ?
            if (p.NoSeat != Table.NoSeatCurrentPlayer)
            {
                LogManager.Log(LogLevel.Warning, "PokerGame.PlayMoney", "{0} just played but it wasn't his turn", p.Name); 
                return false;
            }

            //The Player is Folding
            if (amnt == -1)
            {
                LogManager.Log(LogLevel.MessageLow, "PokerGame.PlayMoney", "{0} FOLDED", p.Name);
                FoldPlayer(p);
                ContinueBettingRound();
                return true;
            }

            var amntNeeded = Table.CallAmnt(p);

            //Validation: Is the player betting under what he needs to Call ?
            if (amnt < amntNeeded)
            {
                //Validation: Can the player bet more ? If yes, this is not fair.
                if (p.CanBet(amnt + 1))
                {
                    LogManager.Log(LogLevel.Warning, "PokerGame.PlayMoney", "{0} needed to play at least {1} and tried {2}", p.Name, amntNeeded, amnt);
                    return false;
                }

                //Else, well, that's ok, the player is All-In !
                amntNeeded = amnt;
            }

            if (amnt > amntNeeded && amnt < Table.MinRaiseAmnt(p)) 
            {
                LogManager.Log(LogLevel.Warning, "PokerGame.PlayMoney", "{0} needed to play at least {1} to raise and tried {2}", p.Name, amntNeeded, amnt);
                return false;
            }

            //Let's hope the player has enough money ! Time to Bet!
            if (!p.TryBet(amnt))
            {
                LogManager.Log(LogLevel.Warning, "PokerGame.PlayMoney", "{0} just put more money than he actually have ({1} > {2})", p.Name, amnt, p.MoneySafeAmnt);
                return false;
            }

            //Update the MinimumRaiseAmount
            Table.MinimumRaiseAmount = Math.Max(Table.MinimumRaiseAmount, p.MoneyBetAmnt);

            //Is the player All-In?
            if (p.MoneySafeAmnt == 0)
            {
                LogManager.Log(LogLevel.MessageVeryLow, "PokerGame.PlayMoney", "Player now All-In !");
                p.State = PlayerStateEnum.AllIn;
                Table.NbAllIn++;
                GameTable.AddAllInCap(p.MoneyBetAmnt);
            }

            //Hmmm ... More Money !! 
            Table.TotalPotAmnt += amnt;

            //Was it a CALL or a RAISE ?
            if (amnt == amntNeeded)
            {
                LogManager.Log(LogLevel.MessageLow, "PokerGame.PlayMoney", "{0} CALLED WITH ${1}", p.Name, amnt);
                CallPlayer(p, amnt);
            }
            else
            {
                LogManager.Log(LogLevel.MessageLow, "PokerGame.PlayMoney", "{0} RAISED WITH ${1}", p.Name, amnt);
                RaisePlayer(p, amnt);
            }

            // Ok this player received enough attention !
            ContinueBettingRound();

            return true;
        }
        private void StartRound()
        {
            switch (m_RoundState)
            {
                case RoundStateEnum.Cards:
                    StartCardRound();
                    break;
                case RoundStateEnum.Betting:
                    StartBettingRound();
                    break;
                case RoundStateEnum.Cumul:
                    StartCumulRound();
                    break;
            }
        }
        private void StartCumulRound()
        {
            GameTable.ManagePotsRoundEnd();

            Observer.RaiseGameBettingRoundEnded(Table.Round);

            if (Table.NbPlayingAndAllIn <= 1)
                AdvanceToNextGameState(); //Advancing to Showdown State
            else
                AdvanceToNextRound(); //Advancing to Next Round
        }
        private void StartBettingRound()
        {
            Observer.RaiseGameBettingRoundStarted(Table.Round);

            //We Put the current player just before the starting player, then we will take the next player and he will be the first
            Table.ChangeCurrentPlayerTo(Table.GetSeatOfPlayingPlayerJustBefore(Table.SeatOfTheFirstPlayer));
            Table.NbPlayed = 0;
            Table.MinimumRaiseAmount = Table.Params.MoneyUnit;

            WaitALittle(Params.WaitingTimes.AfterBoardDealed);

            if (Table.NbPlaying <= 1)
                EndBettingRound();
            else
                ContinueBettingRound();
        }
        private void StartCardRound()
        {
            switch (Table.Round)
            {
                case RoundTypeEnum.Preflop:
                    DealHole();
                    break;
                case RoundTypeEnum.Flop:
                    DealFlop();
                    break;
                case RoundTypeEnum.Turn:
                    DealTurn();
                    break;
                case RoundTypeEnum.River:
                    DealRiver();
                    break;
            }

            AdvanceToNextRoundState(); // Advance to Betting Round State
        }
        private void DealRiver()
        {
            GameTable.AddCards(GameTable.Dealer.DealRiver().ToString());
        }
        private void DealTurn()
        {
            GameTable.AddCards(GameTable.Dealer.DealTurn().ToString());
        }
        private void DealFlop()
        {
            GameTable.AddCards(GameTable.Dealer.DealFlop().Select(x => x.ToString()).ToArray());
        }
        private void DealHole()
        {
            foreach (var p in Table.PlayingAndAllInPlayers)
            {
                p.HoleCards = GameTable.Dealer.DealHoles().Select(x => x.ToString()).ToArray();
                Observer.RaisePlayerHoleCardsChanged(p);
            }
        }
        private void ShowAllCards()
        {
            foreach (var p in Table.Players)
                if (p.IsPlaying || p.IsAllIn)
                {
                    p.IsShowingCards = true;
                    Observer.RaisePlayerHoleCardsChanged(p);
                }
            AdvanceToNextGameState(); //Advancing to DecideWinners State
        }
        private void FoldPlayer(PlayerInfo p)
        {
            if(p.State != PlayerStateEnum.Zombie)
                p.State = PlayerStateEnum.SitIn;

            WaitALittle(Params.WaitingTimes.AfterPlayerAction);

            Observer.RaisePlayerActionTaken(p, GameActionEnum.Fold, -1);
        }
        private void CallPlayer(PlayerInfo p, int played)
        {
            Table.NbPlayed++;

            WaitALittle(Params.WaitingTimes.AfterPlayerAction);

            Observer.RaisePlayerActionTaken(p, GameActionEnum.Call, played);
        }
        private void RaisePlayer(PlayerInfo p, int played)
        {
            // Since every raise "restart" the round, 
            // the number of players who played is the number of AllIn players plus the raising player
            Table.NbPlayed = Table.NbAllIn;
            if (!p.IsAllIn)
                Table.NbPlayed++;

            Table.HigherBet = p.MoneyBetAmnt;

            WaitALittle(Params.WaitingTimes.AfterPlayerAction);

            Observer.RaisePlayerActionTaken(p, GameActionEnum.Raise, played);
        }
        private void ContinueBettingRound()
        {
            if (Table.NbPlayingAndAllIn == 1 || Table.NbPlayed >= Table.NbPlayingAndAllIn)
                EndBettingRound();
            else
                ChooseNextPlayer();
        }
        private void EndBettingRound()
        {
            AdvanceToNextRoundState(); // Advance to Cumul Round State
        }
        private void ChooseNextPlayer()
        {
            var next = Table.GetSeatOfPlayingPlayerNextTo(Table.CurrentPlayerSeat);

            Table.ChangeCurrentPlayerTo(next);

            Observer.RaisePlayerActionNeeded(next.Player);

            if (next.Player.IsZombie)
            {
                if (Table.CanCheck(next.Player))
                    PlayMoney(next.Player, 0);
                else
                    PlayMoney(next.Player, -1);
            }
        }
        private void DistributeMoney()
        {
            foreach (var pot in Table.Pots)
            {
                var players = pot.AttachedPlayers;
                if (players.Length > 0)
                {
                    var wonAmount = pot.Amount / players.Length;
                    if (wonAmount > 0)
                    {
                        foreach (var p in players)
                        {
                            p.Player.MoneySafeAmnt += wonAmount;
                            Observer.RaisePlayerWonPot(p, pot, wonAmount);
                            WaitALittle(Params.WaitingTimes.AfterPotWon);
                        }
                    }
                }
            }
        }
        private void DecideWinners()
        {
            GameTable.CleanPotsForWinning();
            AdvanceToNextGameState(); //Advancing to DistributeMoney State
        }
        private void WaitALittle(int waitingTime)
        {
            Thread.Sleep(waitingTime);
        }
        #endregion Private Methods
    }
}
