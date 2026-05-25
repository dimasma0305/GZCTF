import { Alert, Center, SegmentedControl, Stack } from '@mantine/core'
import { mdiFlagOutline, mdiSnowflake, mdiSwordCross } from '@mdi/js'
import Icon from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { AdScoreboardTable } from '@Components/AdScoreboardTable'
import { ScoreboardTable } from '@Components/ScoreboardTable'
import { TeamRank } from '@Components/TeamRank'
import { WithGameTab } from '@Components/WithGameTab'
import { WithNavBar } from '@Components/WithNavbar'
import { AdScoreTimeLine } from '@Components/charts/AdScoreTimeLine'
import { ScoreTimeLine } from '@Components/charts/ScoreTimeLine'
import { MobileScoreboardTable } from '@Components/mobile/ScoreboardTable'
import { useIsMobile } from '@Utils/ThemeOverride'
import { useGameScoreboard, useGameTeamInfo } from '@Hooks/useGame'
import api from '@Api'

const Scoreboard: FC = () => {
  const { id } = useParams()
  const numId = parseInt(id ?? '-1')
  const { teamInfo, error } = useGameTeamInfo(numId)
  const { scoreboard } = useGameScoreboard(numId)
  const { t } = useTranslation()

  const [divisionId, setDivisionId] = useState<number | null>(null)
  const isMobile = useIsMobile(1080)
  const isVertical = useIsMobile()

  // Derive challenge-type presence from teamInfo.challenges (grouped by category;
  // each ChallengeInfo carries `type`). No new backend endpoint needed.
  const { hasJeopardyChallenges, hasAdChallenges } = useMemo(() => {
    const all = Object.values(teamInfo?.challenges ?? {}).flat()
    return {
      hasJeopardyChallenges: all.some((c) => c.type !== 'AttackDefense'),
      hasAdChallenges: all.some((c) => c.type === 'AttackDefense'),
    }
  }, [teamInfo])

  const showTabs = hasJeopardyChallenges && hasAdChallenges
  const [activeTab, setActiveTab] = useState<string>('jeopardy')
  // When game is A&D-only, force the A&D view.
  const effectiveTab = !hasJeopardyChallenges && hasAdChallenges ? 'ad' : activeTab

  // The A&D board freezes independently (it has its own scoreboard endpoint),
  // so on the A&D tab read the freeze state from there — otherwise an A&D-only
  // game would never show the banner. Only fetch when the game has A&D.
  const { data: adScoreboard } = api.game.useGameAdScoreboard(numId, undefined, hasAdChallenges)
  const onAdTab = effectiveTab === 'ad' && hasAdChallenges
  const frozenView = onAdTab ? adScoreboard?.isFrozenView : scoreboard?.isFrozenView
  const frozenAt = onAdTab ? adScoreboard?.freeze : scoreboard?.freeze

  const freezeBanner = frozenView ? (
    <Alert color="blue" icon={<Icon path={mdiSnowflake} size={1} />}>
      {t('game.content.frozen_banner', {
        time: frozenAt ? dayjs(frozenAt).format('LLL') : '',
      })}
    </Alert>
  ) : null

  const tabNavbar = showTabs ? (
    <Center mb="xs" mt="xs">
      <SegmentedControl
        size="sm"
        value={effectiveTab}
        onChange={(v) => v && setActiveTab(v)}
        data={[
          {
            value: 'jeopardy',
            label: (
              <Center style={{ gap: 4 }}>
                <Icon path={mdiFlagOutline} size={0.8} />
                <span>{t('game.content.scoreboard.tab.jeopardy', 'Jeopardy')}</span>
              </Center>
            ),
          },
          {
            value: 'ad',
            label: (
              <Center style={{ gap: 4 }}>
                <Icon path={mdiSwordCross} size={0.8} />
                <span>{t('game.content.scoreboard.tab.ad', 'Attack & Defense')}</span>
              </Center>
            ),
          },
        ]}
      />
    </Center>
  ) : null

  const showJeopardy = effectiveTab === 'jeopardy' && hasJeopardyChallenges
  const showAd = effectiveTab === 'ad' && hasAdChallenges

  return (
    <WithNavBar width="90%" minWidth={0}>
      {isMobile ? (
        <Stack pt="md">
          {freezeBanner}
          {teamInfo && !error && <TeamRank />}
          {tabNavbar}
          {showAd ? (
            <>
              <AdScoreTimeLine divisionName={null} />
              <AdScoreboardTable numId={numId} />
            </>
          ) : isVertical ? (
            <MobileScoreboardTable divisionId={divisionId} setDivisionId={setDivisionId} />
          ) : (
            <ScoreboardTable divisionId={divisionId} setDivisionId={setDivisionId} />
          )}
        </Stack>
      ) : (
        <WithGameTab>
          <Stack pb="2rem">
            {freezeBanner}
            {tabNavbar}
            {showAd ? (
              <>
                <AdScoreTimeLine divisionName={null} />
                <AdScoreboardTable numId={numId} />
              </>
            ) : (
              <>
                {showJeopardy && <ScoreTimeLine divisionId={divisionId} />}
                <ScoreboardTable divisionId={divisionId} setDivisionId={setDivisionId} />
              </>
            )}
          </Stack>
        </WithGameTab>
      )}
    </WithNavBar>
  )
}

export default Scoreboard
