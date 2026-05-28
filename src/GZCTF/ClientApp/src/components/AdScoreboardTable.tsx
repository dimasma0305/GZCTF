import {
  Badge,
  Box,
  Center,
  Group,
  Pagination,
  Paper,
  Stack,
  Table,
  Text,
  Tooltip,
  useMantineTheme,
} from '@mantine/core'
import { mdiHeartPulse, mdiShieldHalfFull, mdiSwordCross, mdiTimerSandComplete } from '@mdi/js'
import { Icon } from '@mdi/react'
import cx from 'clsx'
import { FC, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import {
  AdLikeCategoryHeaderRow,
  AdLikeHiddenCols,
  AdLikePinnedHeaderCells,
  AdLikePinnedRowCells,
  AdLikeToolbar,
  ITEM_COUNT_PER_PAGE,
  adLikeRowHighlight,
  fmtPts,
  statusColor,
  useAdLikeScoreboardState,
} from '@Components/AdLikeScoreboard'
import { useAdScoreboard, useGame } from '@Hooks/useGame'
import { AdScoreboardChallenge } from '@Api'
import misc from '@Styles/Misc.module.css'
import classes from '@Styles/ScoreboardTable.module.css'

// Fixed sub-column widths (px) for each service group. With table-layout:fixed
// the tier-2 icon header + the body numbers share these widths, so a tight
// icon sits directly over its number — the icons cluster instead of spreading
// across one wide cell.
const SUBCOL = { atk: 40, sla: 46, def: 42, status: 56 }

// A challenge group spans its 4 metric sub-columns. Bound the tier-2 name to
// this width so a long challenge title truncates within its own group instead
// of overflowing into the neighbouring challenge's header.
const GROUP_W = SUBCOL.atk + SUBCOL.sla + SUBCOL.def + SUBCOL.status

interface AdScoreboardTableProps {
  numId: number
}

export const AdScoreboardTable: FC<AdScoreboardTableProps> = ({ numId }) => {
  const { t } = useTranslation()
  const theme = useMantineTheme()
  const { adScoreboard } = useAdScoreboard(numId)
  const { game } = useGame(numId)

  // Group the service columns by category (the backend already orders them
  // contiguously) so the header can render a colored category tier like the
  // jeopardy board.
  const challengeGroups = useMemo(() => {
    const out: { category: string; items: AdScoreboardChallenge[] }[] = []
    for (const ch of adScoreboard?.challenges ?? []) {
      const last = out[out.length - 1]
      if (last && last.category === ch.category) last.items.push(ch)
      else out.push({ category: ch.category, items: [ch] })
    }
    return out
  }, [adScoreboard])
  const myTeamName = game?.teamName ?? null

  const {
    activePage,
    setPage,
    setDivisionName,
    keyword,
    setKeyword,
    highlightedTeam,
    divisionOptions,
    selectValue,
    hasDivisionFilter,
    allRank,
    filteredList,
    base,
    currentItems,
    findMyTeam,
  } = useAdLikeScoreboardState(adScoreboard?.teams, myTeamName)

  if (!adScoreboard || adScoreboard.teams.length === 0 || adScoreboard.latestRound === 0) {
    return (
      <Paper shadow="md" p="xl">
        <Stack align="center" gap="xs">
          <Icon path={mdiSwordCross} size={2.5} color="var(--mantine-color-dimmed)" />
          <Text fw="bold" c="dimmed">
            {t('game.content.scoreboard.ad.empty.title', 'No A&D rounds yet')}
          </Text>
          <Text size="sm" c="dimmed">
            {t(
              'game.content.scoreboard.ad.empty.description',
              'The board will fill in once the first round completes.'
            )}
          </Text>
        </Stack>
      </Paper>
    )
  }

  return (
    <Paper shadow="md" p="md">
      <Stack gap="xs">
        <AdLikeToolbar
          divisionOptions={divisionOptions}
          selectValue={selectValue}
          hasDivisionFilter={hasDivisionFilter}
          onDivisionChange={setDivisionName}
          myTeamName={myTeamName}
          onFindMyTeam={findMyTeam}
          keyword={keyword}
          onKeywordChange={setKeyword}
          latestRoundText={t('game.content.scoreboard.ad.latest_round', {
            round: adScoreboard.latestRound,
            defaultValue: 'through round {{round}}',
          })}
        />

        <Box pos="relative" mih="calc(100vh - 14rem)">
          <Table.ScrollContainer minWidth="100%" classNames={{ scrollContainer: misc.noScrollBars }}>
            <Table className={classes.table} verticalSpacing={4} horizontalSpacing={8}>
              <Table.Thead className={classes.thead}>
                {/* Tier 1 — colored category groups (shared with KotH board). */}
                <AdLikeCategoryHeaderRow groups={challengeGroups} subColsPerItem={4} />
                {/* Tier 2 — challenge name, spanning its 4 metric sub-columns. */}
                <Table.Tr>
                  <AdLikeHiddenCols />
                  {(adScoreboard.challenges ?? []).map((ch) => (
                    <Table.Th key={ch.challengeId} colSpan={4} className={cx(classes.mono, classes.groupStart)}>
                      <Tooltip label={ch.title} withinPortal>
                        <Text size="xs" fw={700} truncate maw={GROUP_W} mx="auto">
                          {ch.title}
                        </Text>
                      </Tooltip>
                    </Table.Th>
                  ))}
                </Table.Tr>
                {/* Tier 3 — pinned column labels + metric icons (attack / SLA /
                    defense / status) as narrow sub-columns. */}
                <Table.Tr>
                  <AdLikePinnedHeaderCells
                    countLabel={t('game.content.scoreboard.ad.column.captures', 'Captures')}
                    totalLabel={t('game.content.scoreboard.ad.column.total', 'Total')}
                  />
                  {(adScoreboard.challenges ?? []).flatMap((ch) => [
                    <Table.Th key={`${ch.challengeId}-a`} className={cx(classes.mono, classes.groupStart)} style={{ width: SUBCOL.atk }}>
                      <Tooltip label={t('game.content.scoreboard.ad.legend.attack', 'Attack')} withinPortal>
                        <Center><Icon path={mdiSwordCross} size={0.6} color={theme.colors.teal[6]} /></Center>
                      </Tooltip>
                    </Table.Th>,
                    <Table.Th key={`${ch.challengeId}-s`} className={classes.mono} style={{ width: SUBCOL.sla }}>
                      <Tooltip label={t('game.content.scoreboard.ad.legend.sla', 'SLA')} withinPortal>
                        <Center><Icon path={mdiTimerSandComplete} size={0.6} color={theme.colors.blue[6]} /></Center>
                      </Tooltip>
                    </Table.Th>,
                    <Table.Th key={`${ch.challengeId}-d`} className={classes.mono} style={{ width: SUBCOL.def }}>
                      <Tooltip label={t('game.content.scoreboard.ad.legend.defense', 'Defense loss')} withinPortal>
                        <Center><Icon path={mdiShieldHalfFull} size={0.6} color={theme.colors.red[6]} /></Center>
                      </Tooltip>
                    </Table.Th>,
                    <Table.Th key={`${ch.challengeId}-st`} className={classes.mono} style={{ width: SUBCOL.status }}>
                      <Tooltip label={t('game.content.scoreboard.ad.column.status', 'Status')} withinPortal>
                        <Center><Icon path={mdiHeartPulse} size={0.6} color="var(--mantine-color-dimmed)" /></Center>
                      </Tooltip>
                    </Table.Th>,
                  ])}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {currentItems.map((row, idx) => {
                  const isHighlighted = highlightedTeam === row.teamName
                  const tableRank = base + idx + 1
                  return (
                    <Table.Tr
                      key={row.participationId}
                      data-team-name={row.teamName}
                      style={adLikeRowHighlight(isHighlighted, theme)}
                    >
                      {/* Pinned columns — shared with KotH board. Captures is the
                          A&D "count" column; Total is the headline rollup. */}
                      <AdLikePinnedRowCells
                        rank={row.rank}
                        teamName={row.teamName}
                        division={row.division}
                        total={row.total}
                        allRank={allRank}
                        tableRank={tableRank}
                        countValue={row.flagsCaptured}
                      />

                      {/* Per-service cells — four real sub-columns (attack / SLA /
                          defense / status) per challenge, aligned under the icon
                          headers. */}
                      {(adScoreboard.challenges ?? []).flatMap((ch) => {
                        const svc = row.services?.find((s) => s.challengeId === ch.challengeId)
                        if (!svc) {
                          return [
                            <Table.Td key={ch.challengeId} colSpan={4} className={cx(classes.mono, classes.groupStart)}>
                              <Text size="xs" c="dimmed">
                                {t('game.content.scoreboard.ad.no_service_cell', 'no service')}
                              </Text>
                            </Table.Td>,
                          ]
                        }
                        return [
                          <Table.Td key={`${ch.challengeId}-a`} className={cx(classes.mono, classes.groupStart)}>
                            <Text size="xs" c="teal" className={misc.ffmono} fw={700}>
                              {fmtPts(svc.attackPoints)}
                            </Text>
                          </Table.Td>,
                          <Table.Td key={`${ch.challengeId}-s`} className={classes.mono}>
                            <Text size="xs" c="blue" className={misc.ffmono} fw={700}>
                              {fmtPts(svc.slaPoints)}
                            </Text>
                          </Table.Td>,
                          <Table.Td key={`${ch.challengeId}-d`} className={classes.mono}>
                            <Text
                              size="xs"
                              c={svc.defenseLoss > 0 ? 'red' : 'dimmed'}
                              className={misc.ffmono}
                              fw={700}
                            >
                              {svc.defenseLoss > 0 ? `−${fmtPts(svc.defenseLoss)}` : '0'}
                            </Text>
                          </Table.Td>,
                          <Table.Td key={`${ch.challengeId}-st`} className={classes.mono}>
                            <Badge
                              size="xs"
                              variant="light"
                              color={statusColor(svc.lastCheckStatus)}
                              px={5}
                              styles={{ root: { textTransform: 'none' }, label: { fontSize: 9 } }}
                            >
                              {svc.lastCheckStatus === 'InternalError'
                                ? 'Error'
                                : (svc.lastCheckStatus ??
                                  t('game.content.scoreboard.ad.cell.no_check', 'n/a'))}
                            </Badge>
                          </Table.Td>,
                        ]
                      })}
                      {/* matches the flexible spacer column in the header */}
                      <Table.Td aria-hidden />
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>

          {filteredList.length === 0 && (
            <Center mih="6rem">
              <Text size="sm" c="dimmed">
                {t('game.content.scoreboard.ad.no_teams', 'No teams ranked yet.')}
              </Text>
            </Center>
          )}
        </Box>

        {/* Footer — stats legend + tip on the left, pagination on the right. */}
        <Group justify="space-between" align="flex-start" wrap="wrap" gap="md">
          <Stack gap={4}>
            <Tooltip.Group>
              <Group gap="lg">
                <Tooltip
                  label={t(
                    'game.content.scoreboard.ad.legend.attack_tip',
                    'Points gained from flags captured from other teams.'
                  )}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiSwordCross} size={0.8} color={theme.colors.teal[6]} />
                    <Text size="sm" c="teal">
                      {t('game.content.scoreboard.ad.legend.attack', 'Attack')}
                    </Text>
                  </Group>
                </Tooltip>
                <Tooltip
                  label={t(
                    'game.content.scoreboard.ad.legend.defense_tip',
                    'Points lost from your services being captured by others.'
                  )}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiShieldHalfFull} size={0.8} color={theme.colors.red[6]} />
                    <Text size="sm" c="red">
                      {t('game.content.scoreboard.ad.legend.defense', 'Defense loss')}
                    </Text>
                  </Group>
                </Tooltip>
                <Tooltip
                  label={t(
                    'game.content.scoreboard.ad.legend.sla_tip',
                    'Service-level availability points from passing checks.'
                  )}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiTimerSandComplete} size={0.8} color={theme.colors.blue[6]} />
                    <Text size="sm" c="blue">
                      {t('game.content.scoreboard.ad.legend.sla', 'SLA')}
                    </Text>
                  </Group>
                </Tooltip>
              </Group>
            </Tooltip.Group>
            <Text size="xs" c="dimmed">
              {t(
                'game.content.scoreboard.ad.tip',
                'Total = Attack + SLA − Defense loss. Updated after every check + every accepted attack.'
              )}
            </Text>
          </Stack>
          <Pagination
            value={activePage}
            onChange={setPage}
            total={Math.max(1, Math.ceil(filteredList.length / ITEM_COUNT_PER_PAGE))}
            boundaries={2}
          />
        </Group>
      </Stack>
    </Paper>
  )
}
