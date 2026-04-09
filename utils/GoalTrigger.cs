/// Attach this to the trigger collider inside each goal.
/// Set 'scoringTeam' to identify which team scores when ball enters this goal.
/// 
/// SETUP:
///   - Blue goal object   → GoalTrigger (scoringTeam = PurpleTeam)  [purple scores here]
///   - Purple goal object → GoalTrigger (scoringTeam = BlueTeam)    [blue scores here]
using UnityEngine;

public class GoalTrigger : MonoBehaviour
{
    public enum Team { BlueTeam, PurpleTeam }

    [Tooltip("The team that SCORES when the ball enters this goal.")]
    public Team scoringTeam;

    private SoccerEnvController m_Controller;

    void Start()
    {
        // Find the controller in the parent environment area
        m_Controller = GetComponentInParent<SoccerEnvController>();
        if (m_Controller == null)
            m_Controller = FindObjectOfType<SoccerEnvController>();
    }

    void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("ball")) return;
        if (m_Controller == null) return;

        if (scoringTeam == Team.BlueTeam)
            m_Controller.BlueScored();
        else
            m_Controller.PurpleScored();
    }
}