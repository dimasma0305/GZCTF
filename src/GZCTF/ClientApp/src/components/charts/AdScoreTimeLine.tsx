import { FC } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { RoundScoreTimeLine } from '@Components/charts/RoundScoreTimeLine'
import { useAdTimeline } from '@Hooks/useGame'

interface AdScoreTimeLineProps {
  /** Division-name filter; null = all divisions. */
  divisionName: string | null
}

/**
 * Per-round score chart for the A&D scoreboard — a thin wrapper that feeds the
 * A&D /Timeline endpoint into the shared <see cref="RoundScoreTimeLine" />.
 */
export const AdScoreTimeLine: FC<AdScoreTimeLineProps> = ({ divisionName }) => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1')
  const { t } = useTranslation()

  const { adTimeline } = useAdTimeline(numId)

  return (
    <RoundScoreTimeLine
      timeline={adTimeline}
      divisionName={divisionName}
      yAxisName={t('game.label.score', 'Score')}
    />
  )
}
