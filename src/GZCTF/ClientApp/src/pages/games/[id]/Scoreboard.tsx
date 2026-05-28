import { Alert, Center, SegmentedControl, Stack } from '@mantine/core'
import { mdiCrown, mdiFlagOutline, mdiSnowflake, mdiSwordCross } from '@mdi/js'
import Icon from '@mdi/react'
import dayjs from 'dayjs'
import { FC, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useParams } from 'react-router'
import { AdScoreboardTable } from '@Components/AdScoreboardTable'
import { KothScoreboardTable } from '@Components/KothScoreboardTable'
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

  // Derive presence of each engine from teamInfo.challenges. The three boards
  // are independent: jeopardy uses ScoreboardTable, A&D uses AdScoreboardTable
  // (which includes hills as columns alongside services), KotH uses the new
  // dedicated KothScoreboardTable from /Ad/Koth/Scoreboard.
  const { hasJeopardyChallenges, hasAdChallenges, hasKothChallenges } = useMemo(() => {
    const all = Object.values(teamInfo?.challenges ?? {}).flat()
    return {
      hasJeopardyChallenges: all.some((c) => c.type !== 'AttackDefense' && c.type !== 'KingOfTheHill'),
      hasAdChallenges: all.some((c) => c.type === 'AttackDefense'),
      hasKothChallenges: all.some((c) => c.type === 'KingOfTheHill'),
    }
  }, [teamInfo])

  const presentTabs = (hasJeopardyChallenges ? 1 : 0) + (hasAdChallenges ? 1 : 0) + (hasKothChallenges ? 1 : 0)
  const showTabs = presentTabs >= 2
  // Default tab in priority: jeopardy if present, else AD, else KotH.
  const defaultTab = hasJeopardyChallenges ? 'jeopardy' : hasAdChallenges ? 'ad' : 'koth'
  const [activeTab, setActiveTab] = useState<string>(defaultTab)
  // Coerce to a tab that's actually available (kind toggled off after first render).
  const effectiveTab =
    (activeTab === 'jeopardy' && !hasJeopardyChallenges)
      || (activeTab === 'ad' && !hasAdChallenges)
      || (activeTab === 'koth' && !hasKothChallenges)
      ? defaultTab
      : activeTab

  // Each live board freezes independently (separate endpoints) — read the
  // freeze state from whichever board we're currently showing.
  const { data: adScoreboard } = api.game.useGameAdScoreboard(numId, undefined, hasAdChallenges)
  const onAdTab = effectiveTab === 'ad' && hasAdChallenges
  const onKothTab = effectiveTab === 'koth' && hasKothChallenges
  const frozenView = onAdTab ? adScoreboard?.isFrozenView
    : onKothTab ? false  /* KotH board freeze flag is on its own response; banner handled inline */
    : scoreboard?.isFrozenView
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
          ...(hasJeopardyChallenges ? [{
            value: 'jeopardy',
            label: (
              <Center style={{ gap: 4 }}>
                <Icon path={mdiFlagOutline} size={0.8} color="var(--mantine-color-blue-6)" />
                <span>{t('game.content.scoreboard.tab.jeopardy', 'Jeopardy')}</span>
              </Center>
            ),
          }] : []),
          ...(hasAdChallenges ? [{
            value: 'ad',
            label: (
              <Center style={{ gap: 4 }}>
                <Icon path={mdiSwordCross} size={0.8} color="var(--mantine-color-red-6)" />
                <span>{t('game.content.scoreboard.tab.ad', 'Attack & Defense')}</span>
              </Center>
            ),
          }] : []),
          ...(hasKothChallenges ? [{
            value: 'koth',
            label: (
              <Center style={{ gap: 4 }}>
                <Icon path={mdiCrown} size={0.8} color="var(--mantine-color-violet-6)" />
                <span>{t('game.content.scoreboard.tab.koth', 'King of the Hill')}</span>
              </Center>
            ),
          }] : []),
        ]}
      />
    </Center>
  ) : null

  const showJeopardy = effectiveTab === 'jeopardy' && hasJeopardyChallenges
  const showAd = effectiveTab === 'ad' && hasAdChallenges
  const showKoth = effectiveTab === 'koth' && hasKothChallenges

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
          ) : showKoth ? (
            <KothScoreboardTable numId={numId} />
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
            ) : showKoth ? (
              <KothScoreboardTable numId={numId} />
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
