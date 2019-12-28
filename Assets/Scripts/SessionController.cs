﻿/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 *
 * Author: Nuno Fachada
 * */

using System;
using UnityEngine;
using System.Collections.Generic;

public class SessionController
    : MonoBehaviour, IMatchDataProvider, ISessionDataProvider
{
    private struct Match
    {
        public readonly IPlayer player1;
        public readonly IPlayer player2;
        public IPlayer this[PColor color] => color == PColor.White ? player1
                : color == PColor.Red ? player2
                    : throw new InvalidOperationException(
                        $"Invalid player color");

        public Match Swapped => new Match(player2, player1);
        public bool IsDummy => player1 is DummyPlayer || player2 is DummyPlayer;
        public Match(IPlayer player1, IPlayer player2)
        {
            this.player1 = player1;
            this.player2 = player2;
        }
        public override string ToString() => $"{player1} vs {player2}";
    }

    private struct DummyPlayer : IPlayer
    {
        public bool IsHuman => false;
        public string PlayerName => "Dummy";
        public IThinker Thinker => null;
    }

    [SerializeField] private GameObject matchPrefab = null;
    [SerializeField] private int rows = 7;
    [SerializeField] private int cols = 7;
    [SerializeField] private int winSequence = 4;
    [SerializeField] private int roundPiecesPerPlayer = 10;
    [SerializeField] private int squarePiecesPerPlayer = 11;

    [Tooltip("Maximum real time that AI can take to play")]
    [SerializeField] private float aITimeLimit = 0.5f;

    [Tooltip("Even if the AI plays immediately, this time gives the "
        + "illusion that the AI took some minimum time to play")]
    [SerializeField] private float minAIGameMoveTime = 0.25f;

    private SessionView view;
    private Board board;
    private Match nextMatch;
    private ICollection<Match> allMatches;
    private IEnumerator<Match> matchEnumerator;

    private GameObject matchInstance = null;
    private MatchController matchController = null;

    private SessionState state;

    private IList<IPlayer> activeAIs = null;

    private IPlayer humanPlayer;

    // Variables which define how the UI will behave
    private bool uiShowListOfMatches;
    private bool uiShowTournamentStandings;
    private bool uiWhoPlaysFirst;
    private bool uiBlockStartNextMatch;
    private bool uiBlockShowResult;

    // UI listener methods
    private Action preMatchAction;
    private Action swapPlayersAction;

    // Awake is called when the script instance is being loaded
    private void Awake()
    {
        // Get all active AIs and put them in a list
        List<IPlayer> allAIs = new List<IPlayer>();
        GetComponents(allAIs);
        activeAIs = allAIs.FindAll(ai => (ai as AIPlayer).IsActive);

        // Get reference to the UI (session view)
        view = GetComponent<SessionView>();

        // Setup the UI listener methods
        preMatchAction = () => state = SessionState.PreMatch;
        swapPlayersAction = () => nextMatch = nextMatch.Swapped;

        // Set the session state to Begin
        state = SessionState.Begin;

        // Instantiate a human player if there are not enough AIs to do a match
        if (activeAIs.Count < 2)
            humanPlayer = new HumanPlayer();

        // Instantiate the matches list
        allMatches = new List<Match>();

        // Setup session depending on how many AIs will play
        if (activeAIs.Count == 0)
        {
            // A game between human players
            uiShowListOfMatches = false;
            uiShowTournamentStandings = false;
            uiWhoPlaysFirst = false;
            uiBlockStartNextMatch = true;
            uiBlockShowResult = true;
            allMatches.Add(new Match(humanPlayer, humanPlayer));
        }
        else if (activeAIs.Count == 1)
        {
            // A game between a human and an AI
            uiShowListOfMatches = false;
            uiShowTournamentStandings = false;
            uiWhoPlaysFirst = true;
            uiBlockStartNextMatch = false;
            uiBlockShowResult = true;
            allMatches.Add(new Match(humanPlayer, activeAIs[0]));
        }
        else if (activeAIs.Count == 2)
        {
            // A game between two AIs, ask who plays first
            uiShowListOfMatches = false;
            uiShowTournamentStandings = false;
            uiWhoPlaysFirst = true;
            uiBlockStartNextMatch = false;
            uiBlockShowResult = true;
            allMatches.Add(new Match(activeAIs[0], activeAIs[1]));
        }
        else
        {
            // Multiple AIs, run a tournament
            uiShowListOfMatches = true;
            uiShowTournamentStandings = true;
            uiWhoPlaysFirst = false;
            uiBlockStartNextMatch = false;
            uiBlockShowResult = false;

            // In this mode we need an even number of players to set up the
            // matches, so add a fake one if necessary
            if (activeAIs.Count % 2 != 0) activeAIs.Add(new DummyPlayer());

            // Setup matches using the round-robin method
            // https://en.wikipedia.org/wiki/Round-robin_tournament
            for (int i = 1; i < activeAIs.Count; i++)
            {
                // This will be the AI to swap position after each round
                IPlayer aiToSwapPosition;

                // Set up matches for current round i
                for (int j = 0; j < activeAIs.Count / 2; j++)
                {
                    // This is match j for current round i
                    Match match = new Match(
                        activeAIs[j], activeAIs[j + activeAIs.Count / 2]);
                    // Only add match to match list if it's not a dummy match
                    if (!match.IsDummy)
                    {
                        // Each match is in practice two matches, so both AIs
                        // can have a match where they are the first to play
                        allMatches.Add(match);
                        allMatches.Add(match.Swapped);
                    }
                }
                // Swap AI positions for next round
                aiToSwapPosition = activeAIs[activeAIs.Count - 1];
                activeAIs.RemoveAt(activeAIs.Count - 1);
                activeAIs.Insert(1, aiToSwapPosition);
            }
        }

        // Get the match enumerator and initialize it
        matchEnumerator = allMatches.GetEnumerator();
        matchEnumerator.MoveNext();
        nextMatch = matchEnumerator.Current;
    }

    private void OnEnable()
    {
        // Register state change methods with UI events
        view.PreMatch += preMatchAction;
        view.SwapPlayers += swapPlayersAction;
        view.StartNextMatch += StartNextMatch;
        view.MatchClear += DestroyAndIterateMatch;
        view.EndSession += EndSession;
    }

    private void OnDisable()
    {
        // Unregister state change methods with UI events
        view.PreMatch -= preMatchAction;
        view.SwapPlayers -= swapPlayersAction;
        view.StartNextMatch -= StartNextMatch;
        view.MatchClear -= DestroyAndIterateMatch;
        view.EndSession -= EndSession;
    }

    private void StartNextMatch()
    {
        // Instantiate a board for the next match
        board = new Board(rows, cols, winSequence,
            roundPiecesPerPlayer, squarePiecesPerPlayer);

        // Instantiate the next match
        matchInstance = Instantiate(matchPrefab, transform);
        matchInstance.name = $"Match{PlayerWhite}VS{PlayerRed}";

        // Get a reference to the match controller of the next match
        matchController = matchInstance.GetComponent<MatchController>();

        // Add a listener for the match over event
        matchController.MatchOver.AddListener(EndCurrentMatch);

        // Set state to InMatch
        state = SessionState.InMatch;
    }

    private void EndCurrentMatch()
    {
        state = SessionState.PostMatch;
    }

    private void DestroyAndIterateMatch()
    {
        Destroy(matchInstance);
        if (matchEnumerator.MoveNext())
        {
            nextMatch = matchEnumerator.Current;
        }
        else
        {
            state = SessionState.End;
        }
    }

    private void EndSession()
    {
        UnityEditor.EditorApplication.isPlaying = false;
    }

    // Implementation of IMatchDataProvider
    public Board Board => board;
    public IPlayer CurrentPlayer => nextMatch[board.Turn];
    public float AITimeLimit => aITimeLimit;
    public float TimeBetweenAIMoves => minAIGameMoveTime;
    public IPlayer GetPlayer(PColor player) => nextMatch[player];

    // Implementation of ISessionDataProvider
    public SessionState State => state;
    public string PlayerWhite => nextMatch[PColor.White].PlayerName;
    public string PlayerRed => nextMatch[PColor.Red].PlayerName;
    public Winner LastMatchResult => matchController?.Result ?? Winner.None;
    public string WinnerString => matchController?.WinnerString;
    public bool ShowListOfMatches => uiShowListOfMatches;
    public bool ShowTournamentStandings => uiShowTournamentStandings;
    public bool WhoPlaysFirst => uiWhoPlaysFirst;
    public bool BlockStartNextMatch => uiBlockStartNextMatch;
    public bool BlockShowResult => uiBlockShowResult;
}
