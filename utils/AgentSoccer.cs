/// NEW vs default:
///   - Stamina system (drains with speed, recovers at rest, limits force when low)
///   - 2 extra vector observations appended to the default raycasts:
///       [0] normalised stamina (0-1)
///       [1] normalised goal-diff clipped to [-5, +5]
///   - Selfish mode: agents rewarded only for their own ball contacts/goals;
///     proximity to teammate is mildly penalised (ball-hogging emerges naturally)
///   - Coop mode:   agents also rewarded for spatial spread from teammate
///   - Confidence / "bus-parking" mode: when leading by >= confidenceThreshold
///     goals, agents are rewarded for staying in their own half
///
/// SETUP IN INSPECTOR:
///   - Behavior Parameters > Vector Observation Space Size = 2
///     (raycasts are handled automatically; this adds the 2 new dims)
///   - Assign 'centerLineZ': Z-coordinate of the pitch centre line
///   - Assign 'ownHalfSign': +1 if own goal is at positive Z, -1 otherwise
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

public class AgentSoccer : Agent
{
    // ---------------------------------------------------------------
    //  Inspector configuration
    // ---------------------------------------------------------------
    [Header("Field Geometry")]
    [Tooltip("World-space Z of the centre line. Adjust to match your pitch.")]
    public float centerLineZ = 0f;

    [Tooltip("+1 if this agent's own goal is in the +Z direction, -1 for -Z.")]
    public float ownHalfSign = 1f;

    [Header("Stamina")]
    [Range(0.001f, 0.01f)]
    [Tooltip("Stamina lost per unit of speed per FixedUpdate step.")]
    public float staminaDrainRate = 0.004f;

    [Range(0.001f, 0.01f)]
    [Tooltip("Stamina recovered per step when nearly stationary.")]
    public float staminaRecoveryRate = 0.003f;

    [Range(0f, 1f)]
    [Tooltip("Below this stamina the agent's movement force is scaled down.")]
    public float staminaLowThreshold = 0.25f;

    [Header("Movement")]
    public float moveForce     = 2000f;
    public float rotationSpeed = 2.5f;
    public float kickForce     = 2000f;

    // ---------------------------------------------------------------
    //  Runtime state (set by SoccerEnvController each step)
    // ---------------------------------------------------------------
    [HideInInspector] public int   goalDiff;
    [HideInInspector] public bool  selfishMode;
    [HideInInspector] public int   confidenceThreshold;

    // ---------------------------------------------------------------
    //  Private fields
    // ---------------------------------------------------------------
    private Rigidbody m_Rb;
    private float     m_Stamina = 1f;
    private bool      m_TouchedBallThisStep = false;

    // Reference to the teammate (populated in Start via parent transform)
    private AgentSoccer m_Teammate;

    /// <summary>Exposed for RewardUI — read-only stamina in [0,1].</summary>
    public float StaminaNormalized => m_Stamina;

    // ---------------------------------------------------------------
    //  Unity / MLAgents lifecycle
    // ---------------------------------------------------------------
    public override void Initialize()
    {
        m_Rb = GetComponent<Rigidbody>();
    }

    void Start()
    {
        // Find teammate: other AgentSoccer in the same parent object
        foreach (var a in GetComponentsInParent<AgentSoccer>())
        {
            if (a != this) { m_Teammate = a; break; }
        }
        if (m_Teammate == null)
        {
            // Fallback: search siblings under the same parent
            if (transform.parent != null)
            {
                foreach (var a in transform.parent.GetComponentsInChildren<AgentSoccer>())
                {
                    if (a != this) { m_Teammate = a; break; }
                }
            }
        }
    }

    // ---------------------------------------------------------------
    //  Called every step by SoccerEnvController
    // ---------------------------------------------------------------
    public void UpdateControllerState(int diff, bool selfish, int confThreshold)
    {
        goalDiff            = diff;
        selfishMode         = selfish;
        confidenceThreshold = confThreshold;
    }

    // ---------------------------------------------------------------
    //  Observations
    //  Default Soccer Twos uses raycasts (no vector obs).
    //  We append 2 values → set Space Size = 2 in Behavior Parameters.
    // ---------------------------------------------------------------
    public override void CollectObservations(VectorSensor sensor)
    {
        // [0] Normalised stamina  (0 = exhausted, 1 = full)
        sensor.AddObservation(m_Stamina);

        // [1] Normalised goal differential, clipped to [-5, +5] → [-1, +1]
        sensor.AddObservation(Mathf.Clamp(goalDiff / 5f, -1f, 1f));
    }

    // ---------------------------------------------------------------
    //  Actions
    // ---------------------------------------------------------------
    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        var discreteActions = actionBuffers.DiscreteActions;

        // ---------- Stamina update ----------
        float speed = m_Rb.velocity.magnitude;
        m_Stamina -= speed * staminaDrainRate;
        if (speed < 0.5f)
            m_Stamina += staminaRecoveryRate;
        m_Stamina = Mathf.Clamp01(m_Stamina);

        // ---------- Movement ----------
        float staminaScale = m_Stamina < staminaLowThreshold
            ? Mathf.InverseLerp(0f, staminaLowThreshold, m_Stamina)
            : 1f;

        MoveAgent(discreteActions, staminaScale);

        // ---------- Per-step reward shaping ----------
        ApplyStepRewards();
    }

    // ---------------------------------------------------------------
    //  Per-step reward logic
    // ---------------------------------------------------------------
    void ApplyStepRewards()
    {
        // 1. Existential penalty — encourages urgency
        AddReward(-0.001f);

        // 2. Confidence / "Bus-parking" mechanic
        //    If we're leading by >= threshold, reward staying in own half.
        if (goalDiff >= confidenceThreshold)
        {
            bool inOwnHalf = (ownHalfSign > 0)
                ? transform.localPosition.z > centerLineZ
                : transform.localPosition.z < centerLineZ;

            AddReward(inOwnHalf ? 0.002f : -0.002f);
        }

        // 3. Selfish vs Coop spatial reward
        if (m_Teammate != null)
        {
            float distToTeammate = Vector3.Distance(
                transform.localPosition, m_Teammate.transform.localPosition);

            if (selfishMode)
            {
                // Penalise clustering — makes ball-hogging emerge
                if (distToTeammate < 3f)
                    AddReward(-0.0005f);
            }
            else
            {
                // Reward spreading out across the pitch (coop coverage)
                float normDist = Mathf.Clamp(distToTeammate / 10f, 0f, 1f);
                AddReward(normDist * 0.001f);
            }
        }

        // 4. Stamina penalty when exhausted — teaches energy-efficient movement
        if (m_Stamina < staminaLowThreshold)
            AddReward(-0.0005f);
    }

    // ---------------------------------------------------------------
    //  Ball contact
    // ---------------------------------------------------------------
    void OnCollisionEnter(Collision collision)
    {
        if (!collision.gameObject.CompareTag("ball")) return;

        m_TouchedBallThisStep = true;

        if (selfishMode)
        {
            // In selfish mode: only the touching agent benefits
            AddReward(0.1f);
        }
        else
        {
            // In coop mode: touching agent gets a smaller reward;
            // the group reward from the controller handles the rest
            AddReward(0.05f);
        }
    }

    // ---------------------------------------------------------------
    //  Goal events — called by SoccerEnvController
    // ---------------------------------------------------------------
    public void OnGoalScored(bool scoredByMyTeam, int goalDiff)
    {
        if (scoredByMyTeam)
        {
            // In selfish mode the individual reward is the whole signal;
            // in coop mode the group reward in the controller covers most of it
            AddReward(selfishMode ? 1.0f : 0.3f);
        }
        else
        {
            AddReward(selfishMode ? -1.0f : -0.3f);
        }
        // EndEpisode is called via the SimpleMultiAgentGroup in the controller
    }

    // ---------------------------------------------------------------
    //  Reset (called by SoccerEnvController.ResetAgent)
    // ---------------------------------------------------------------
    public void OnEpisodeReset()
    {
        m_Stamina             = 1f;
        m_TouchedBallThisStep = false;
    }

    // ---------------------------------------------------------------
    //  Movement — same logic as default Soccer Twos, stamina-scaled
    // ---------------------------------------------------------------
    void MoveAgent(ActionSegment<int> act, float staminaScale)
    {
        var dirToGo    = Vector3.zero;
        var rotateDir  = Vector3.zero;
        bool kick      = false;

        // Discrete action branches (must match Behavior Parameters setup):
        //   Branch 0: forward/back  (0=none, 1=fwd, 2=back)
        //   Branch 1: rotate        (0=none, 1=left, 2=right)
        //   Branch 2: kick          (0=none, 1=kick)
        int forwardAction = act[0];
        int rotateAction  = act[1];
        int kickAction    = act[2];

        switch (forwardAction)
        {
            case 1: dirToGo  =  transform.forward; break;
            case 2: dirToGo  = -transform.forward; break;
        }

        switch (rotateAction)
        {
            case 1: rotateDir = -transform.up; break;
            case 2: rotateDir =  transform.up; break;
        }

        kick = kickAction == 1;

        m_Rb.AddForce(dirToGo * moveForce * staminaScale, ForceMode.Force);
        transform.Rotate(rotateDir * rotationSpeed, Space.World);

        if (kick)
        {
            var ballRb = GameObject.FindGameObjectWithTag("ball")?.GetComponent<Rigidbody>();
            if (ballRb != null)
            {
                var toball = (ballRb.transform.position - transform.position).normalized;
                float distToBall = Vector3.Distance(transform.position, ballRb.transform.position);
                if (distToBall < 2f)
                    ballRb.AddForce(toball * kickForce * staminaScale, ForceMode.Impulse);
            }
        }
    }

    // ---------------------------------------------------------------
    //  Heuristic (keyboard control for testing)
    // ---------------------------------------------------------------
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discreteActionsOut = actionsOut.DiscreteActions;

        discreteActionsOut[0] = Input.GetKey(KeyCode.W) ? 1 :
                                 Input.GetKey(KeyCode.S) ? 2 : 0;

        discreteActionsOut[1] = Input.GetKey(KeyCode.A) ? 1 :
                                 Input.GetKey(KeyCode.D) ? 2 : 0;

        discreteActionsOut[2] = Input.GetKey(KeyCode.Space) ? 1 : 0;
    }
}