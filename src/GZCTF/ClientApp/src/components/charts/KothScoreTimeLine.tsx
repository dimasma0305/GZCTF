import { FC } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { RoundScoreTimeLine } from '@Components/charts/RoundScoreTimeLine'
import { useKothTimeline } from '@Hooks/useGame'

interface KothScoreTimeLineProps {
  /** Division-name filter; null = all divisions. */
  divisionName: string | null
}

/**
 * Per-round score chart for the King of the Hill scoreboard — a thin wrapper
 * that feeds the KotH-only /ad/koth/timeline endpoint into the shared
 * <see cref="RoundScoreTimeLine" />. The endpoint returns the same shape as
 * the A&D timeline, so the y-axis is hold credit only.
 */
export const KothScoreTimeLine: FC<KothScoreTimeLineProps> = ({ divisionName }) => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1')
  const { t } = useTranslation()

  const { kothTimeline } = useKothTimeline(numId)

  return (
    <RoundScoreTimeLine
      timeline={kothTimeline}
      divisionName={divisionName}
      yAxisName={t('game.label.koth_hold_points', 'Hold points')}
    />
  )
}
