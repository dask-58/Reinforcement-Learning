/// Manages a single Soccer environment area.
///
/// NEW vs default:
///   - Tracks per-team score within an episode
///   - Exposes selfishMode toggle (Inspector checkbox)
///   - Exposes confidenceThreshold (Inspector int)
///   - Broadcasts goal-diff to all agents on every goal
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;

public class SoccerEnvController : MonoBehaviour
{
    [System.Serializable]
    public class PlayerInfo
    {
        public AgentSoccer Agent;
        [HideInInspector] public Vector3 StartingPos;
        [HideInInspector] public Quaternion StartingRot;
        [HideInInspector] public Rigidbody Rb;
    }

    // ---------------------------------------------------------------
    //  Inspector-visible experiment knobs
    // ---------------------------------------------------------------
    [Header("Experiment Mode")]
    [Tooltip("Selfish: agents are rewarded only for their own ball contacts / goals.\n" +
             "Coop: agents are also rewarded for spreading out and assisting.")]
    public bool selfishMode = false;

    [Tooltip("If a team leads by this many goals they enter 'bus-parking' mode:\n" +
             "agents are rewarded for staying in their own half.")]
    public int confidenceThreshold = 2;

    // ---------------------------------------------------------------
    //  Standard Soccer Twos fields (same as default)
    // ---------------------------------------------------------------
    [Header("Max Environment Steps")]
    public int MaxEnvironmentSteps = 25000;

    [Header("Scene Objects")]
    public GameObject Ball;
    [HideInInspector] public Rigidbody BallRb;
    public GameObject blueGoal;   // assign in Inspector
    public GameObject purpleGoal; // assign in Inspector

    [Header("Teams")]
    public List<PlayerInfo> BlueAgents  = new List<PlayerInfo>();
    public List<PlayerInfo> PurpleAgents = new List<PlayerInfo>();

    // ---------------------------------------------------------------
    //  Internal state
    // ---------------------------------------------------------------
    private SimpleMultiAgentGroup m_BlueAgentGroup;
    private SimpleMultiAgentGroup m_PurpleAgentGroup;

    private int m_ResetTimer;
    private Vector3 m_BallStartPos;

    // Per-episode scores (reset in OnEpisodeBegin)
    [HideInInspector] public int BlueScore;
    [HideInInspector] public int PurpleScore;

    // ---------------------------------------------------------------
    //  Unity lifecycle
    // ---------------------------------------------------------------
    void Start()
    {
        m_BlueAgentGroup   = new SimpleMultiAgentGroup();
        m_PurpleAgentGroup = new SimpleMultiAgentGroup();

        BallRb = Ball.GetComponent<Rigidbody>();
        m_BallStartPos = Ball.transform.position;

        foreach (var p in BlueAgents)
        {
            p.StartingPos = p.Agent.transform.position;
            p.StartingRot = p.Agent.transform.rotation;
            p.Rb          = p.Agent.GetComponent<Rigidbody>();
            m_BlueAgentGroup.RegisterAgent(p.Agent);
        }

        foreach (var p in PurpleAgents)
        {
            p.StartingPos = p.Agent.transform.position;
            p.StartingRot = p.Agent.transform.rotation;
            p.Rb          = p.Agent.GetComponent<Rigidbody>();
            m_PurpleAgentGroup.RegisterAgent(p.Agent);
        }

        ResetScene();
    }

    void FixedUpdate()
    {
        m_ResetTimer++;
        if (m_ResetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
        {
            // Time-limit: draw — end episode with no group reward
            m_BlueAgentGroup.GroupEpisodeInterrupted();
            m_PurpleAgentGroup.GroupEpisodeInterrupted();
            ResetScene();
        }

        // Push per-agent observations that require controller state
        BroadcastControllerState();
    }

    // ---------------------------------------------------------------
    //  Goal events — call these from a trigger collider on each goal
    //  (attach a GoalTrigger component that references this controller)
    // ---------------------------------------------------------------

    /// <summary>Blue scored in Purple's goal.</summary>
    public void BlueScored()
    {
        BlueScore++;
        int goalDiffBlue   =  BlueScore - PurpleScore;
        int goalDiffPurple = -goalDiffBlue;

        // Group rewards
        if (!selfishMode)
        {
            m_BlueAgentGroup.AddGroupReward(1f);
            m_PurpleAgentGroup.AddGroupReward(-1f);
        }

        // Per-agent rewards (always applied regardless of mode)
        foreach (var p in BlueAgents)
            p.Agent.OnGoalScored(scoredByMyTeam: true, goalDiff: goalDiffBlue);

        foreach (var p in PurpleAgents)
            p.Agent.OnGoalScored(scoredByMyTeam: false, goalDiff: goalDiffPurple);

        m_BlueAgentGroup.EndGroupEpisode();
        m_PurpleAgentGroup.EndGroupEpisode();
        ResetScene();
    }

    /// <summary>Purple scored in Blue's goal.</summary>
    public void PurpleScored()
    {
        PurpleScore++;
        int goalDiffPurple =  PurpleScore - BlueScore;
        int goalDiffBlue   = -goalDiffPurple;

        if (!selfishMode)
        {
            m_PurpleAgentGroup.AddGroupReward(1f);
            m_BlueAgentGroup.AddGroupReward(-1f);
        }

        foreach (var p in PurpleAgents)
            p.Agent.OnGoalScored(scoredByMyTeam: true, goalDiff: goalDiffPurple);

        foreach (var p in BlueAgents)
            p.Agent.OnGoalScored(scoredByMyTeam: false, goalDiff: goalDiffBlue);

        m_PurpleAgentGroup.EndGroupEpisode();
        m_BlueAgentGroup.EndGroupEpisode();
        ResetScene();
    }

    // ---------------------------------------------------------------
    //  Internal helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Push controller-level state into each agent every physics step.
    /// Agents use this to shape their per-step rewards.
    /// </summary>
    void BroadcastControllerState()
    {
        int goalDiffBlue   =  BlueScore - PurpleScore;
        int goalDiffPurple = -goalDiffBlue;

        foreach (var p in BlueAgents)
            p.Agent.UpdateControllerState(goalDiffBlue,   selfishMode, confidenceThreshold);

        foreach (var p in PurpleAgents)
            p.Agent.UpdateControllerState(goalDiffPurple, selfishMode, confidenceThreshold);
    }

    void ResetScene()
    {
        m_ResetTimer = 0;
        BlueScore    = 0;
        PurpleScore  = 0;

        // Reset ball
        BallRb.velocity        = Vector3.zero;
        BallRb.angularVelocity = Vector3.zero;
        Ball.transform.position = m_BallStartPos;

        // Reset agents
        foreach (var p in BlueAgents)   ResetAgent(p);
        foreach (var p in PurpleAgents) ResetAgent(p);
    }

    void ResetAgent(PlayerInfo info)
    {
        info.Rb.velocity        = Vector3.zero;
        info.Rb.angularVelocity = Vector3.zero;

        // Small positional jitter so agents don't memorise fixed-start routes
        var offset = new Vector3(
            Random.Range(-1.5f, 1.5f),
            0f,
            Random.Range(-1.5f, 1.5f));

        info.Agent.transform.SetPositionAndRotation(
            info.StartingPos + offset,
            Quaternion.Euler(0f, Random.Range(-10f, 10f) + info.StartingRot.eulerAngles.y, 0f));

        info.Agent.OnEpisodeReset();
    }
}